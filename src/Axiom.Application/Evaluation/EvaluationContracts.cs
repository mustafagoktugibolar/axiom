using System.Collections.Immutable;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;

namespace Axiom.Application.Evaluation;

/// <summary>
/// The wire shape of an evaluation, shared verbatim by REST (<c>EvaluationResult</c> in rest-api.yaml)
/// and MCP (docs/04-contracts/mcp-contract.md). Property names serialize in camelCase.
/// </summary>
public sealed record EvaluationResult(
    string EvaluationId,
    string ReceiptId,
    string Stage,
    string SnapshotId,
    SnapshotDto Snapshot,
    string Verdict,
    bool SignificantChange,
    ImmutableArray<SignificanceTriggerDto> SignificanceTriggers,
    ImmutableSortedDictionary<string, ImmutableArray<string>> ResolvedScope,
    ImmutableArray<string> TopologyGaps,
    ImmutableArray<ApplicableGovernanceDto> ApplicableGovernance,
    ImmutableArray<ActiveExceptionDto> ActiveExceptions,
    ImmutableArray<RequiredCheckDto> RequiredChecks,
    ImmutableArray<string> RequiredActions,
    ImmutableArray<string> RequiredReviewers,
    ImmutableArray<AffectedDependencyDto> AffectedDependencies,
    ImmutableArray<FindingDto> Findings,
    ImmutableArray<PolicyRunDto> PolicyRuns,
    SemanticCoverageDto SemanticCoverage,
    string? ParentEvaluationId,
    string? DesignId,
    string? DesignHash,
    DateTimeOffset CreatedAt)
{
    private static readonly ImmutableDictionary<string, string> ScopeKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["organization"] = "organizations",
        ["domain"] = "domains",
        ["system"] = "systems",
        ["component"] = "components",
        ["repository"] = "repositories",
        ["path"] = "paths",
        ["capability"] = "capabilities",
        ["api"] = "apis",
        ["resource"] = "resources",
        ["technology"] = "technologies",
        ["environment"] = "environments",
        ["changeClass"] = "changeClasses",
    }.ToImmutableDictionary();

    public static EvaluationResult From(StoredEvaluation stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var e = stored.Evaluation;
        return new EvaluationResult(
            e.Id,
            stored.Receipt.Id,
            StageName(e.Stage),
            e.SnapshotId,
            new SnapshotDto(e.SnapshotId, e.SourceCommit, e.SnapshotPublishedAt, e.CatalogVersion),
            VerdictLattice.ToContract(e.Verdict),
            e.Significance.IsSignificant,
            [.. e.Significance.Triggers.Select(t => new SignificanceTriggerDto(t.Code, t.ChangeClass, t.Explanation, t.Evidence, t.SourceRecordId))],
            e.ResolvedScope.Dimensions.ToImmutableSortedDictionary(
                kv => ScopeKeys.GetValueOrDefault(kv.Key, kv.Key),
                kv => kv.Value.Select(DisplayName).ToImmutableArray(),
                StringComparer.Ordinal),
            e.ResolvedScope.Gaps,
            [.. e.AppliedRecords.Select(r => new ApplicableGovernanceDto(
                r.Id, r.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Kind.ToString(), r.Importance.ToString().ToLowerInvariant(),
                r.Title, r.IsPotential, r.WaivedBy, r.PartiallyWaivedBy, r.RefinedBy, r.OverriddenBy))],
            [.. e.AppliedExceptions.Select(x => new ActiveExceptionDto(
                x.Id, x.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), x.ExpiresAt, x.Coverage.ToString().ToLowerInvariant(), x.Targets))],
            [.. e.RequiredChecks.Select(c => new RequiredCheckDto(c.RuleId, c.RecordId, VerdictLattice.ToContract(c.Mode)))],
            e.RequiredActions,
            e.RequiredReviewers,
            [.. e.AffectedDependencies.Select(d => new AffectedDependencyDto(d.Entity, d.Depth, d.Path, d.Owners, d.IsFact))],
            [.. e.Findings.Select(FindingDto.From)],
            [.. e.PolicyRuns.Select(p => new PolicyRunDto(p.RuleId, p.RuleVersion, p.RuleHash, p.RecordId, VerdictLattice.ToContract(p.Mode), p.Outcome.ToString().ToLowerInvariant(), p.Violations.Length, p.Error))],
            new SemanticCoverageDto(e.SemanticCoverage.Status.ToString(), e.SemanticCoverage.Reason, e.SemanticCoverage.Provider, e.SemanticCoverage.Model),
            e.ParentEvaluationId,
            e.DesignId,
            e.DesignHash,
            e.CreatedAt);
    }

    public static string StageName(EvaluationStage stage) => stage switch
    {
        EvaluationStage.Preflight => "PRE_FLIGHT",
        EvaluationStage.Design => "DESIGN",
        EvaluationStage.EditCheck => "EDIT_CHECK",
        EvaluationStage.Diff => "DIFF",
        EvaluationStage.PullRequest => "PR",
        EvaluationStage.Drift => "DRIFT",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static string DisplayName(string value) => EntityRef.TryParse(value, out var reference) ? reference.Name : value;
}

public sealed record SnapshotDto(string Id, string SourceCommit, DateTimeOffset PublishedAt, string CatalogVersion);

public sealed record SignificanceTriggerDto(string Code, string ChangeClass, string Explanation, ImmutableArray<string> Evidence, string? GovernanceId);

public sealed record ApplicableGovernanceDto(
    string Id,
    string Revision,
    string Kind,
    string Importance,
    string Summary,
    bool PotentiallyApplicable,
    ImmutableArray<string> WaivedBy,
    ImmutableArray<string> PartiallyWaivedBy,
    ImmutableArray<string> RefinedBy,
    ImmutableArray<string> OverriddenBy);

public sealed record ActiveExceptionDto(string Id, string Revision, DateTimeOffset ExpiresAt, string Coverage, ImmutableArray<string> Targets);

public sealed record RequiredCheckDto(string RuleId, string GovernanceId, string Mode);

public sealed record AffectedDependencyDto(string Entity, int Depth, ImmutableArray<string> Path, ImmutableArray<string> Owners, bool Confirmed);

public sealed record EvidenceDto(string Source, string? Id, string? Revision, string? Locator, string? Hash);

public sealed record FindingDto(
    string Code,
    string Severity,
    string Message,
    string Source,
    ImmutableArray<string> GovernanceIds,
    ImmutableArray<EvidenceDto> Evidence,
    string? RecommendedAction,
    string? Path,
    int? Line,
    string? RuleId,
    string? RuleVersion,
    double? Confidence,
    string? WaivedBy)
{
    public static FindingDto From(Finding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new FindingDto(
            f.Code, VerdictLattice.ToContract(f.Severity), f.Message, f.Source.ToString().ToLowerInvariant(), f.GovernanceIds,
            [.. f.Evidence.Select(e => new EvidenceDto(e.Source, e.Id, e.Revision, e.Locator, e.Hash))],
            f.RecommendedAction, f.Path, f.Line, f.RuleId, f.RuleVersion, f.Confidence, f.WaivedBy);
    }
}

public sealed record PolicyRunDto(string RuleId, string RuleVersion, string RuleHash, string GovernanceId, string Mode, string Outcome, int Violations, string? Error);

public sealed record SemanticCoverageDto(string Status, string? Reason, string? Provider, string? Model);
