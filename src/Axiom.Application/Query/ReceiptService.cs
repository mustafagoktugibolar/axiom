using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Audit;
using Axiom.Domain.Evaluation;

namespace Axiom.Application.Query;

/// <summary>A receipt as REST and MCP show it. <see cref="Verified"/> is recomputed from the stored payload on every read.</summary>
public sealed record ReceiptDto(
    string ReceiptId,
    string EvaluationId,
    string Stage,
    string Verdict,
    string Repository,
    string? CommitSha,
    string SnapshotId,
    DateTimeOffset IssuedAt,
    string Digest,
    string PreviousChainDigest,
    string ChainDigest,
    bool Verified,
    JsonElement Payload)
{
    public static ReceiptDto From(Receipt r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new ReceiptDto(
            r.Id, r.EvaluationId, EvaluationResult.StageName(r.Stage), VerdictLattice.ToContract(r.Verdict), r.Repository, r.CommitSha, r.SnapshotId,
            r.IssuedAt, r.Digest, r.PreviousChainDigest, r.ChainDigest, ReceiptFactory.Verify(r), JsonDocument.Parse(r.Payload).RootElement.Clone());
    }
}

public sealed record ChainVerificationDto(bool Intact, long Receipts, string? FirstBrokenReceiptId, string HeadChainDigest);

/// <summary>Receipt lookup and hash-chain verification (R17, R18, P8).</summary>
public sealed class ReceiptService(IEvaluationStore store, ISystemGraph graph)
{
    public async Task<ReceiptDto> GetAsync(AxiomPrincipal principal, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadAudit);
        var receipt = await store.FindReceiptAsync(principal.OrganizationId, RequestLimits.Identifier(id, "id"), cancellationToken)
            ?? throw AxiomException.NotFound($"Receipt '{id}'");
        return ReceiptDto.From(receipt);
    }

    public async Task<ReceiptDto> GetByCommitAsync(AxiomPrincipal principal, string repository, string commitSha, EvaluationStage? stage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadAudit);
        var sha = RequestLimits.CommitSha(commitSha, "commitSha", required: true)!;

        // Receipts name the repository as the System Graph does ("repo:gateway"); callers use whatever name they know.
        var name = RequestLimits.Identifier(repository, "repository");
        var bound = await graph.ResolveRepositoryAsync(principal.OrganizationId, name, cancellationToken);
        var receipt = await store.FindReceiptByCommitAsync(principal.OrganizationId, bound?.ToString() ?? name, sha, stage, cancellationToken)
            ?? throw AxiomException.NotFound($"A receipt for {repository}@{sha}");
        return ReceiptDto.From(receipt);
    }

    public async Task<ChainVerificationDto> VerifyChainAsync(AxiomPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadAudit);
        long count = 0;
        var expected = ReceiptFactory.GenesisDigest;
        await foreach (var receipt in store.ReadChainAsync(principal.OrganizationId, cancellationToken))
        {
            count++;
            if (!ReceiptFactory.Verify(receipt) || !string.Equals(receipt.PreviousChainDigest, expected, StringComparison.Ordinal))
            {
                return new ChainVerificationDto(false, count, receipt.Id, expected);
            }

            expected = receipt.ChainDigest;
        }

        return new ChainVerificationDto(true, count, null, expected);
    }
}
