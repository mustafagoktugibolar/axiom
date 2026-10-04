using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;

namespace Axiom.Application.Query;

/// <summary>Input of <c>governance.get_context</c>: an evaluation, or an explicit repository and task.</summary>
public sealed record ContextRequest(
    string? EvaluationId,
    string? Repository,
    string? Ref,
    string? Task,
    IReadOnlyCollection<string>? Paths,
    bool IncludeRationale,
    string? HarnessType,
    string? HarnessSessionId);

public sealed record ContextRecordDto(
    GovernanceRecordDto Record,
    string Importance,
    bool PotentiallyApplicable,
    ImmutableArray<string> WaivedBy,
    ImmutableArray<string> RefinedBy,
    ImmutableArray<string> OverriddenBy);

/// <summary>The bounded set of governance an agent needs to work inside a scope.</summary>
public sealed record ContextBundleDto(
    string EvaluationId,
    string SnapshotId,
    string Verdict,
    ImmutableSortedDictionary<string, ImmutableArray<string>> ResolvedScope,
    ImmutableArray<ContextRecordDto> Decisions,
    ImmutableArray<ContextRecordDto> Standards,
    ImmutableArray<ContextRecordDto> Goals,
    ImmutableArray<ActiveExceptionDto> ActiveExceptions,
    ImmutableArray<AffectedDependencyDto> TopologyNeighbors,
    ImmutableArray<string> KnownFailureModes,
    ImmutableArray<RequiredCheckDto> RequiredChecks,
    bool Truncated);

public sealed class ContextService(IEvaluationStore evaluations, PreflightService preflight, GovernanceReadService governance)
{
    /// <summary>Upper bound on records in one bundle; the rest is reported as truncated, never silently dropped.</summary>
    public const int MaxRecords = 100;

    public async Task<ContextBundleDto> GetAsync(AxiomPrincipal principal, ContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);

        StoredEvaluation stored;
        if (!string.IsNullOrWhiteSpace(request.EvaluationId))
        {
            Authorizer.Demand(principal, AccessRight.ReadGovernance);
            stored = await evaluations.FindAsync(principal.OrganizationId, RequestLimits.Identifier(request.EvaluationId, "evaluationId"), cancellationToken)
                ?? throw AxiomException.NotFound($"Evaluation '{request.EvaluationId}'");
        }
        else if (!string.IsNullOrWhiteSpace(request.Repository) && !string.IsNullOrWhiteSpace(request.Task))
        {
            stored = await preflight.ExecuteAsync(
                principal,
                new PreflightRequest(null, request.Repository, request.Ref ?? "HEAD", request.Task, request.Paths, null, request.HarnessType, request.HarnessSessionId),
                cancellationToken);
        }
        else
        {
            throw AxiomException.Invalid("Provide 'evaluationId', or both 'repository' and 'task'.");
        }

        return await BuildAsync(principal, stored.Evaluation, request.IncludeRationale, cancellationToken);
    }

    private async Task<ContextBundleDto> BuildAsync(AxiomPrincipal principal, EvaluationRecord evaluation, bool includeRationale, CancellationToken cancellationToken)
    {
        var refs = evaluation.AppliedRecords.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var truncated = refs.Count > MaxRecords;

        var records = new List<ContextRecordDto>();
        foreach (var applied in refs.Take(MaxRecords))
        {
            var record = await governance.GetRevisionAsync(principal.OrganizationId, applied.Id, applied.Revision, includeRationale, cancellationToken);
            if (record is not null)
            {
                records.Add(new ContextRecordDto(record, applied.Importance.ToString().ToLowerInvariant(), applied.IsPotential, applied.WaivedBy, applied.RefinedBy, applied.OverriddenBy));
            }
        }

        ImmutableArray<ContextRecordDto> Of(params RecordKind[] kinds) =>
            [.. records.Where(r => kinds.Any(k => k.ToString() == r.Record.Kind))];

        return new ContextBundleDto(
            evaluation.Id,
            evaluation.SnapshotId,
            VerdictLattice.ToContract(evaluation.Verdict),
            EvaluationResult.ContractScope(evaluation.ResolvedScope),
            Of(RecordKind.Decision),
            Of(RecordKind.Standard),
            Of(RecordKind.Principle, RecordKind.Goal),
            [.. evaluation.AppliedExceptions.Select(x => new ActiveExceptionDto(
                x.Id, x.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), x.ExpiresAt, x.Coverage.ToString().ToLowerInvariant(), x.Targets))],
            [.. evaluation.AffectedDependencies.Select(d => new AffectedDependencyDto(d.Entity, d.Depth, d.Path, d.Owners, d.IsFact))],
            [.. records.Where(r => r.OverriddenBy.IsEmpty).SelectMany(r => r.Record.Forbidden.Select(f => $"{r.Record.Id}: {f}"))],
            [.. evaluation.RequiredChecks.Select(c => new RequiredCheckDto(c.RuleId, c.RecordId, VerdictLattice.ToContract(c.Mode)))],
            truncated);
    }
}
