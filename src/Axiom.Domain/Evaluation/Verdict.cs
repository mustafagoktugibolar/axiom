using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Evaluation;

/// <summary>Verdict lattice (design.md §4.7). The most restrictive non-waived result wins.</summary>
public enum Verdict
{
    Allow = 0,
    AllowWithWarnings = 1,
    RequireReview = 2,
    Block = 3,
}

public enum EvaluationStage
{
    Preflight,
    Design,
    EditCheck,
    Diff,
    PullRequest,
    Drift,
}

/// <summary>Which part of the pipeline produced a finding. Only deterministic sources may block by default.</summary>
public enum FindingSource
{
    /// <summary>Scope/authority resolution, including authoritative conflicts.</summary>
    Resolution,

    /// <summary>A mandatory precondition is missing (design, approval, receipt, topology).</summary>
    Precondition,

    /// <summary>A deterministic policy rule.</summary>
    Policy,

    /// <summary>LLM-assisted analysis. Advisory unless an organization policy explicitly permits blocking.</summary>
    Semantic,
}

/// <summary>Pointer to the evidence behind a finding (R19: what evidence was used).</summary>
public sealed record EvidenceRef(string Source, string? Id = null, string? Revision = null, string? Locator = null, string? Hash = null);

/// <summary>
/// A single explainable result. Every finding states what applies, why, the evidence, and what resolves it.
/// </summary>
public sealed record Finding
{
    public required string Code { get; init; }

    public required EnforcementLevel Severity { get; init; }

    public required string Message { get; init; }

    public required FindingSource Source { get; init; }

    public ImmutableArray<string> GovernanceIds { get; init; } = [];

    public ImmutableArray<EvidenceRef> Evidence { get; init; } = [];

    public string? RecommendedAction { get; init; }

    public string? Path { get; init; }

    public int? Line { get; init; }

    public string? RuleId { get; init; }

    public string? RuleVersion { get; init; }

    /// <summary>Model confidence in [0,1]; only present on semantic findings.</summary>
    public double? Confidence { get; init; }

    /// <summary>ID of the exception that waives this finding, when one applies.</summary>
    public string? WaivedBy { get; init; }

    public bool IsWaived => WaivedBy is not null;
}

public static class VerdictLattice
{
    public static Verdict From(EnforcementLevel level) => level switch
    {
        EnforcementLevel.Info => Verdict.Allow,
        EnforcementLevel.Warn => Verdict.AllowWithWarnings,
        EnforcementLevel.RequireReview => Verdict.RequireReview,
        EnforcementLevel.Block => Verdict.Block,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static Verdict Join(Verdict left, Verdict right) => (Verdict)Math.Max((int)left, (int)right);

    /// <summary>Most restrictive non-waived finding wins; no findings means <see cref="Verdict.Allow"/>.</summary>
    public static Verdict Combine(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Where(f => !f.IsWaived).Select(f => From(f.Severity)).Aggregate(Verdict.Allow, Join);
    }

    /// <summary>Wire representation used by the REST and MCP contracts.</summary>
    public static string ToContract(Verdict verdict) => verdict switch
    {
        Verdict.Allow => "ALLOW",
        Verdict.AllowWithWarnings => "ALLOW_WITH_WARNINGS",
        Verdict.RequireReview => "REQUIRE_REVIEW",
        Verdict.Block => "BLOCK",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    public static string ToContract(EnforcementLevel level) => level switch
    {
        EnforcementLevel.Info => "INFO",
        EnforcementLevel.Warn => "WARN",
        EnforcementLevel.RequireReview => "REQUIRE_REVIEW",
        EnforcementLevel.Block => "BLOCK",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}
