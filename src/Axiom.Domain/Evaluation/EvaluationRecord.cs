using System.Collections.Immutable;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;

namespace Axiom.Domain.Evaluation;

/// <summary>Who asked for the evaluation, and through which harness.</summary>
public sealed record ActorIdentity(string Subject, string? DisplayName, string? HarnessType, string? HarnessSessionId);

/// <summary>Source-control coordinates the evaluation is about. Merge gates always carry an immutable commit SHA.</summary>
public sealed record ScmCoordinates(string Repository, string? Ref, string? CommitSha, string? BaseSha, string? PullRequest);

/// <summary>Exact revision of a governance record that took part in an evaluation (P7).</summary>
public sealed record AppliedRecordRef(
    string Id,
    int Revision,
    string ContentHash,
    RecordKind Kind,
    string Title,
    Importance Importance,
    bool IsPotential,
    ImmutableArray<string> WaivedBy,
    ImmutableArray<string> PartiallyWaivedBy,
    ImmutableArray<string> RefinedBy,
    ImmutableArray<string> OverriddenBy)
{
    public static AppliedRecordRef From(AppliedRecord applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        return new AppliedRecordRef(
            applied.Revision.Id, applied.Revision.Revision, applied.Record.ContentHash, applied.Record.Kind, applied.Record.Title,
            applied.Importance, applied.IsPotential, applied.WaivedBy, applied.PartiallyWaivedBy, applied.RefinedBy, applied.OverriddenBy);
    }
}

public sealed record AppliedExceptionRef(string Id, int Revision, string ContentHash, DateTimeOffset ExpiresAt, Containment Coverage, ImmutableArray<string> Targets)
{
    public static AppliedExceptionRef From(AppliedWaiver waiver)
    {
        ArgumentNullException.ThrowIfNull(waiver);
        return new AppliedExceptionRef(
            waiver.Revision.Id, waiver.Revision.Revision, waiver.Revision.Record.ContentHash,
            waiver.Revision.Record.Exception!.ExpiresAt, waiver.Coverage, waiver.WaivedTargets);
    }
}

/// <summary>A deterministic check that will run on the diff because an applicable record binds it.</summary>
public sealed record RequiredCheck(string RuleId, string RecordId, EnforcementLevel Mode);

/// <summary>The scope a change was resolved to, plus topology gaps that limited the resolution (R4).</summary>
public sealed record ResolvedScopeView(ImmutableSortedDictionary<string, ImmutableArray<string>> Dimensions, ImmutableArray<string> Gaps)
{
    public static ResolvedScopeView From(EvaluationScope scope, IEnumerable<string> gaps)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var dimensions = scope.KnownDimensions.ToImmutableSortedDictionary(
            d => char.ToLowerInvariant(d.ToString()[0]) + d.ToString()[1..],
            d => scope[d].Values.ToImmutableArray(),
            StringComparer.Ordinal);
        return new ResolvedScopeView(dimensions, [.. gaps]);
    }
}

public enum SemanticCoverageStatus
{
    /// <summary>The stage does not use semantic analysis or the organization has not enabled it.</summary>
    NotRequested,
    Complete,

    /// <summary>Semantic analysis was wanted but unavailable; deterministic results are still valid (R24).</summary>
    Degraded,

    /// <summary>The scope forbids sending content to an external provider.</summary>
    ProhibitedByScope,
}

public sealed record SemanticCoverage(SemanticCoverageStatus Status, string? Reason, string? Provider, string? Model)
{
    public static SemanticCoverage NotRequested { get; } = new(SemanticCoverageStatus.NotRequested, null, null, null);
}

/// <summary>An entity outside the direct scope that the change can affect, with the path that reaches it.</summary>
public sealed record AffectedDependency(string Entity, int Depth, ImmutableArray<string> Path, ImmutableArray<string> Owners, bool IsFact);

/// <summary>
/// A completed evaluation. Immutable once created; it is the full explanation of a verdict and the
/// input from which the receipt is derived.
/// </summary>
public sealed record EvaluationRecord
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required EvaluationStage Stage { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required ActorIdentity Actor { get; init; }

    public required ScmCoordinates Scm { get; init; }

    public string? Task { get; init; }

    /// <summary>Hash of the normalized request; identical requests against identical state share it.</summary>
    public required string RequestFingerprint { get; init; }

    public required string SnapshotId { get; init; }

    public required string SourceCommit { get; init; }

    public required DateTimeOffset SnapshotPublishedAt { get; init; }

    public required string CatalogVersion { get; init; }

    /// <summary>The earlier evaluation this one continues (preflight → design → diff → pull request).</summary>
    public string? ParentEvaluationId { get; init; }

    public string? DesignId { get; init; }

    public string? DesignHash { get; init; }

    public required ResolvedScopeView ResolvedScope { get; init; }

    public ImmutableArray<AppliedRecordRef> AppliedRecords { get; init; } = [];

    public ImmutableArray<AppliedExceptionRef> AppliedExceptions { get; init; } = [];

    public ImmutableArray<RequiredCheck> RequiredChecks { get; init; } = [];

    public ImmutableArray<PolicyRunResult> PolicyRuns { get; init; } = [];

    public ImmutableArray<Finding> Findings { get; init; } = [];

    public required SignificanceAssessment Significance { get; init; }

    public ImmutableArray<string> RequiredActions { get; init; } = [];

    public ImmutableArray<string> RequiredReviewers { get; init; } = [];

    public ImmutableArray<AffectedDependency> AffectedDependencies { get; init; } = [];

    public SemanticCoverage SemanticCoverage { get; init; } = SemanticCoverage.NotRequested;

    public ImmutableArray<TraceEntry> Trace { get; init; } = [];

    public required Verdict Verdict { get; init; }
}
