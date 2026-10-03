using System.Collections.Immutable;

namespace Axiom.Domain.Governance;

public enum RecordKind
{
    Principle,
    Standard,
    Decision,
    Goal,
    Exception,
}

/// <summary>Lifecycle states (R2). <see cref="Expired"/> is only meaningful for exceptions.</summary>
public enum LifecycleStatus
{
    Proposed,
    Accepted,
    Deprecated,
    Superseded,
    Rejected,
    Expired,
}

/// <summary>Authority levels, strongest first. Authority is not specificity.</summary>
public enum AuthorityLevel
{
    RegulatorySecurityHard = 0,
    Organization = 1,
    Domain = 2,
    System = 3,
    Component = 4,
    Repository = 5,
    Advisory = 6,
}

/// <summary>Enforcement levels (R9), least restrictive first.</summary>
public enum EnforcementLevel
{
    Info = 0,
    Warn = 1,
    RequireReview = 2,
    Block = 3,
}

/// <summary>How a superseding record expects existing code to migrate.</summary>
public enum MigrationMode
{
    Immediate,
    NewCodeOnly,
    Phased,
    DateBound,
}

public sealed record Authority(AuthorityLevel Level, bool Exemptable);

/// <summary>Binding of an executable rule to a record. Parameters are rule-specific and immutable.</summary>
public sealed record RuleBinding(string RuleId, EnforcementLevel Mode, ImmutableSortedDictionary<string, string> Parameters)
{
    public bool Equals(RuleBinding? other) =>
        other is not null
        && string.Equals(RuleId, other.RuleId, StringComparison.Ordinal)
        && Mode == other.Mode
        && Parameters.SequenceEqual(other.Parameters);

    public override int GetHashCode() => HashCode.Combine(RuleId, Mode, Parameters.Count);
}

public sealed record Enforcement(EnforcementLevel DefaultVerdict, ImmutableArray<RuleBinding> Rules)
{
    /// <summary>The most restrictive level this record can produce on its own.</summary>
    public EnforcementLevel Ceiling => Rules.IsDefaultOrEmpty
        ? DefaultVerdict
        : (EnforcementLevel)Math.Max((int)DefaultVerdict, Rules.Max(r => (int)r.Mode));
}

public enum RelationKind
{
    Supersedes,
    SupersededBy,
    Refines,
    Implements,
    ConflictsWith,
    Related,
    MotivatedBy,
    Requires,
    ExceptionTo,
}

public sealed record Relation(RelationKind Kind, string TargetId);

public sealed record Validity(DateOnly? EffectiveFrom, DateOnly? EffectiveUntil, DateOnly? ReviewAfter)
{
    public static Validity Unbounded { get; } = new(null, null, null);

    public bool IsEffectiveOn(DateOnly date) =>
        (EffectiveFrom is null || date >= EffectiveFrom) && (EffectiveUntil is null || date <= EffectiveUntil);

    public bool IsReviewOverdueOn(DateOnly date) => ReviewAfter is not null && date > ReviewAfter;
}

/// <summary>Terms that exist only on exception records (R10, ADR-0006).</summary>
public sealed record ExceptionTerms(
    ImmutableArray<string> Targets,
    string Rationale,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    ImmutableArray<string> Approvers,
    string? TrackingIssue,
    ImmutableArray<string> CompensatingControls)
{
    /// <summary>The window is half-open: active from <see cref="StartsAt"/> up to, not including, <see cref="ExpiresAt"/>.</summary>
    public bool IsActiveAt(DateTimeOffset instant) => instant >= StartsAt && instant < ExpiresAt;
}

/// <summary>Evidence a non-authoritative candidate must carry (knowledge-onboarding workflow).</summary>
public sealed record CandidateEvidence(
    string SourceLocator,
    string ExcerptHash,
    string ExtractionMethod,
    double Confidence,
    string? SuggestedOwner);

/// <summary>
/// One revision of a governance record as parsed from the authoritative Git source.
/// Instances are immutable; a change to a record is a new revision with a new content hash.
/// </summary>
public sealed record GovernanceRecord
{
    public required string Id { get; init; }

    public required RecordKind Kind { get; init; }

    public required string SchemaVersion { get; init; }

    public required string Title { get; init; }

    public required ImmutableArray<string> Owners { get; init; }

    public ImmutableArray<string> Tags { get; init; } = [];

    public required LifecycleStatus Status { get; init; }

    public required Scope Scope { get; init; }

    public required Authority Authority { get; init; }

    public required Enforcement Enforcement { get; init; }

    /// <summary>The normative statement: the decision, standard, principle, goal or exception rationale.</summary>
    public required string Statement { get; init; }

    public ImmutableArray<string> Forbidden { get; init; } = [];

    public ImmutableArray<string> Preferred { get; init; } = [];

    public ImmutableArray<Relation> Relations { get; init; } = [];

    public Validity Validity { get; init; } = Validity.Unbounded;

    public ExceptionTerms? Exception { get; init; }

    /// <summary>For deprecated records: whether they still govern code that already exists.</summary>
    public bool GovernsExistingCode { get; init; }

    public MigrationMode? Migration { get; init; }

    /// <summary>False when content in this scope must never be sent to an external LLM provider.</summary>
    public bool SemanticExternalAllowed { get; init; } = true;

    public ImmutableArray<CandidateEvidence> Evidence { get; init; } = [];

    /// <summary>Markdown rationale accompanying the structured fields.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>SHA-256 of the canonical source bytes; identifies this exact revision's content.</summary>
    public required string ContentHash { get; init; }

    public IEnumerable<string> RelatedIds(RelationKind kind) => Relations.Where(r => r.Kind == kind).Select(r => r.TargetId);

    /// <summary>
    /// P1 authority property: only accepted records govern. A deprecated record governs only when it
    /// explicitly says it still applies to existing code. Exceptions are never "governing"; they waive.
    /// </summary>
    public bool IsAuthoritativeOn(DateOnly date) =>
        Kind != RecordKind.Exception
        && (Status == LifecycleStatus.Accepted || (Status == LifecycleStatus.Deprecated && GovernsExistingCode))
        && Validity.IsEffectiveOn(date);

    /// <summary>An exception waives only while accepted and inside its validity window (P5).</summary>
    public bool IsActiveExceptionAt(DateTimeOffset instant) =>
        Kind == RecordKind.Exception
        && Status == LifecycleStatus.Accepted
        && Exception is not null
        && Exception.IsActiveAt(instant);

    /// <summary>A hard control: cannot be waived by a normal exception (R11).</summary>
    public bool IsNonExemptable => !Authority.Exemptable;
}
