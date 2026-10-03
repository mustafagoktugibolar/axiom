using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Audit;
using Axiom.Domain.Evaluation;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.Infrastructure.Audit;

/// <summary>
/// PostgreSQL evaluation and receipt store. Rows are only ever inserted. Receipt issuance is serialized
/// per organization with a transaction-scoped advisory lock so the hash chain has no forks.
/// </summary>
public sealed class EfEvaluationStore(AxiomDbContext db, IEventOutbox outbox) : IEvaluationStore
{
    internal static readonly JsonSerializerOptions DocumentOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<StoredEvaluation?> FindAsync(string organizationId, string evaluationId, CancellationToken cancellationToken)
    {
        var row = await db.Set<EvaluationRow>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.OrganizationId == organizationId && e.Id == evaluationId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var receipt = await db.Set<ReceiptRow>().AsNoTracking()
            .SingleAsync(r => r.OrganizationId == organizationId && r.EvaluationId == evaluationId, cancellationToken);
        return new StoredEvaluation(Deserialize(row.Document), ToReceipt(receipt));
    }

    public async Task<StoredEvaluation> AppendAsync(EvaluationRecord evaluation, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(events);
        var organization = evaluation.OrganizationId;

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"axiom-receipt-chain:" + organization}, 0))", cancellationToken);

            if (await FindAsync(organization, evaluation.Id, cancellationToken) is { } existing)
            {
                await transaction.RollbackAsync(cancellationToken);
                return existing;
            }

            var last = await db.Set<ReceiptRow>().AsNoTracking()
                .Where(r => r.OrganizationId == organization)
                .OrderByDescending(r => r.Sequence)
                .Select(r => new { r.Sequence, r.ChainDigest })
                .FirstOrDefaultAsync(cancellationToken);

            var receipt = ReceiptFactory.Create(evaluation, last?.ChainDigest ?? ReceiptFactory.GenesisDigest);
            db.Set<ReceiptRow>().Add(ToRow(receipt, (last?.Sequence ?? 0) + 1));
            db.Set<EvaluationRow>().Add(ToRow(evaluation, receipt.Id));
            db.Set<EvaluationFindingRow>().AddRange(evaluation.Findings.Select((f, i) => new EvaluationFindingRow
            {
                OrganizationId = organization,
                EvaluationId = evaluation.Id,
                Ordinal = i,
                Code = f.Code,
                Severity = VerdictLattice.ToContract(f.Severity),
                Source = f.Source.ToString(),
                RuleId = f.RuleId,
                Path = f.Path,
                WaivedBy = f.WaivedBy,
                GovernanceIds = [.. f.GovernanceIds],
                CreatedAt = evaluation.CreatedAt,
            }));
            db.Set<EvaluationAppliedRecordRow>().AddRange(evaluation.AppliedRecords
                .Select(r => new EvaluationAppliedRecordRow
                {
                    OrganizationId = organization,
                    EvaluationId = evaluation.Id,
                    RecordId = r.Id,
                    Revision = r.Revision,
                    ContentHash = r.ContentHash,
                    Importance = r.Importance.ToString(),
                    IsException = false,
                    CreatedAt = evaluation.CreatedAt,
                })
                .Concat(evaluation.AppliedExceptions.Select(x => new EvaluationAppliedRecordRow
                {
                    OrganizationId = organization,
                    EvaluationId = evaluation.Id,
                    RecordId = x.Id,
                    Revision = x.Revision,
                    ContentHash = x.ContentHash,
                    Importance = "Exception",
                    IsException = true,
                    CreatedAt = evaluation.CreatedAt,
                })));

            foreach (var integrationEvent in events)
            {
                outbox.Enqueue(integrationEvent);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StoredEvaluation(evaluation, receipt);
        });
    }

    public async Task<Receipt?> FindReceiptAsync(string organizationId, string receiptOrEvaluationId, CancellationToken cancellationToken)
    {
        var row = await db.Set<ReceiptRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.OrganizationId == organizationId && (r.Id == receiptOrEvaluationId || r.EvaluationId == receiptOrEvaluationId), cancellationToken);
        return row is null ? null : ToReceipt(row);
    }

    public async Task<Receipt?> FindReceiptByCommitAsync(string organizationId, string repository, string commitSha, EvaluationStage? stage, CancellationToken cancellationToken)
    {
        var stageName = stage?.ToString();
        var row = await db.Set<ReceiptRow>().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Repository == repository && r.CommitSha == commitSha && (stageName == null || r.Stage == stageName))
            .OrderByDescending(r => r.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToReceipt(row);
    }

    public async Task<(ImmutableArray<EvaluationSummary> Items, int Total)> SearchAsync(string organizationId, EvaluationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = db.Set<EvaluationRow>().AsNoTracking().Where(e => e.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(query.Repository))
        {
            rows = rows.Where(e => e.Repository == query.Repository);
        }

        if (query.Stage is { } stage)
        {
            var name = stage.ToString();
            rows = rows.Where(e => e.Stage == name);
        }

        if (query.Verdict is { } verdict)
        {
            var name = VerdictLattice.ToContract(verdict);
            rows = rows.Where(e => e.Verdict == name);
        }

        if (!string.IsNullOrWhiteSpace(query.CommitSha))
        {
            rows = rows.Where(e => e.CommitSha == query.CommitSha);
        }

        if (query.Since is { } since)
        {
            rows = rows.Where(e => e.CreatedAt >= since);
        }

        if (!string.IsNullOrWhiteSpace(query.GovernanceId))
        {
            var id = query.GovernanceId;
            rows = rows.Where(e => db.Set<EvaluationAppliedRecordRow>().Any(a => a.OrganizationId == organizationId && a.EvaluationId == e.Id && a.RecordId == id));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            .Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 200))
            .Select(e => new
            {
                e.Id, e.Stage, e.Verdict, e.Repository, e.CommitSha, e.PullRequest, e.SnapshotId, e.CreatedAt,
                e.ActorSubject, e.HarnessType, e.SignificantChange, e.FindingCount, e.ReceiptId,
            })
            .ToListAsync(cancellationToken);

        return ([.. page.Select(e => new EvaluationSummary(
            e.Id, Enum.Parse<EvaluationStage>(e.Stage), ParseVerdict(e.Verdict), e.Repository, e.CommitSha, e.PullRequest, e.SnapshotId,
            e.CreatedAt, e.ActorSubject, e.HarnessType, e.SignificantChange, e.FindingCount, e.ReceiptId))], total);
    }

    public async IAsyncEnumerable<Receipt> ReadChainAsync(string organizationId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = db.Set<ReceiptRow>().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId)
            .OrderBy(r => r.Sequence)
            .AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            yield return ToReceipt(row);
        }
    }

    private static EvaluationRecord Deserialize(string document) =>
        JsonSerializer.Deserialize<EvaluationRecord>(document, DocumentOptions)
        ?? throw new InvalidOperationException("Stored evaluation document is empty.");

    private static Verdict ParseVerdict(string contract) => contract switch
    {
        "ALLOW" => Verdict.Allow,
        "ALLOW_WITH_WARNINGS" => Verdict.AllowWithWarnings,
        "REQUIRE_REVIEW" => Verdict.RequireReview,
        "BLOCK" => Verdict.Block,
        _ => throw new InvalidOperationException($"Unknown stored verdict '{contract}'."),
    };

    private static EvaluationRow ToRow(EvaluationRecord e, string receiptId) => new()
    {
        OrganizationId = e.OrganizationId,
        Id = e.Id,
        Stage = e.Stage.ToString(),
        Verdict = VerdictLattice.ToContract(e.Verdict),
        Repository = e.Scm.Repository,
        Ref = e.Scm.Ref,
        CommitSha = e.Scm.CommitSha,
        BaseSha = e.Scm.BaseSha,
        PullRequest = e.Scm.PullRequest,
        SnapshotId = e.SnapshotId,
        CreatedAt = e.CreatedAt,
        ActorSubject = e.Actor.Subject,
        HarnessType = e.Actor.HarnessType,
        SignificantChange = e.Significance.IsSignificant,
        FindingCount = e.Findings.Length,
        RequestFingerprint = e.RequestFingerprint,
        ParentEvaluationId = e.ParentEvaluationId,
        DesignId = e.DesignId,
        ReceiptId = receiptId,
        Document = JsonSerializer.Serialize(e, DocumentOptions),
    };

    private static ReceiptRow ToRow(Receipt r, long sequence) => new()
    {
        OrganizationId = r.OrganizationId,
        Id = r.Id,
        EvaluationId = r.EvaluationId,
        Sequence = sequence,
        Stage = r.Stage.ToString(),
        Verdict = VerdictLattice.ToContract(r.Verdict),
        Repository = r.Repository,
        CommitSha = r.CommitSha,
        SnapshotId = r.SnapshotId,
        IssuedAt = r.IssuedAt,
        Payload = r.Payload,
        Digest = r.Digest,
        PreviousChainDigest = r.PreviousChainDigest,
        ChainDigest = r.ChainDigest,
    };

    private static Receipt ToReceipt(ReceiptRow r) => new(
        r.Id, r.OrganizationId, r.EvaluationId, Enum.Parse<EvaluationStage>(r.Stage), ParseVerdict(r.Verdict), r.Repository, r.CommitSha,
        r.SnapshotId, r.IssuedAt, r.Payload, r.Digest, r.PreviousChainDigest, r.ChainDigest);
}
