using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Resolution;

/// <summary>Precedence classes of design.md §6, strongest first. Exceptions and refinements act between them.</summary>
public enum PrecedenceRank
{
    HardControl = 1,
    AcceptedRecord = 4,
    PrincipleOrGoal = 5,
    Advisory = 6,
}

public enum Importance
{
    Required,
    Recommended,
    Informational,
    Waived,
}

/// <summary>An authoritative record whose scope applies to the change, with everything that modified its effect.</summary>
public sealed record AppliedRecord(
    RecordRevision Revision,
    PrecedenceRank Rank,
    bool IsPotential,
    ImmutableArray<int> Specificity,
    ImmutableArray<string> WaivedBy,
    ImmutableArray<string> PartiallyWaivedBy,
    ImmutableArray<string> RefinedBy,
    ImmutableArray<string> OverriddenBy,
    ImmutableArray<string> Reasons)
{
    public GovernanceRecord Record => Revision.Record;

    /// <summary>False when the record remains visible but no longer constrains the change as a whole.</summary>
    public bool IsEffective => WaivedBy.IsEmpty && RefinedBy.IsEmpty && OverriddenBy.IsEmpty;

    public Importance Importance => !IsEffective
        ? Importance.Waived
        : Record.IsNonExemptable || Record.Enforcement.Ceiling >= EnforcementLevel.RequireReview
            ? Importance.Required
            : Record.Enforcement.Ceiling == EnforcementLevel.Warn ? Importance.Recommended : Importance.Informational;
}

/// <summary>An active exception that waives at least one applicable record for this change.</summary>
public sealed record AppliedWaiver(RecordRevision Revision, Containment Coverage, ImmutableArray<string> WaivedTargets)
{
    /// <summary>True when the exception covers a finding at <paramref name="path"/> for this change.</summary>
    public bool Covers(EvaluationScope change, string path) =>
        ScopeMatcher.Contain(Revision.Record.Scope, change.NarrowedToPath(path)).Containment == Containment.Full;
}

public enum ConflictSource
{
    /// <summary>One record names the other in <c>conflictsWith</c>.</summary>
    Explicit,

    /// <summary>One record forbids exactly what the other prefers.</summary>
    Structural,
}

public static class ConflictCodes
{
    public const string Unresolved = "UNRESOLVED_AUTHORITY_CONFLICT";
    public const string ConfigurationError = "GOVERNANCE_CONFIGURATION_ERROR";
    public const string ResolvedByPrecedence = "CONFLICT_RESOLVED_BY_PRECEDENCE";
}

/// <summary>
/// A conflict between two applicable authoritative records. It is never settled by similarity or
/// model preference (R12): either precedence decides it, or it is surfaced for human resolution.
/// </summary>
public sealed record AuthorityConflict(
    string Code,
    EnforcementLevel Severity,
    string FirstRecordId,
    string SecondRecordId,
    ConflictSource Source,
    string? PrevailingRecordId,
    string Explanation)
{
    public bool IsResolved => PrevailingRecordId is not null;
}

/// <summary>Why a record was or was not selected. Every considered record gets exactly one entry.</summary>
public sealed record TraceEntry(
    string RecordId,
    int Revision,
    RecordKind Kind,
    bool Selected,
    ImmutableArray<string> Reasons,
    ImmutableArray<int> Specificity);

public sealed record ResolutionResult(
    ImmutableArray<AppliedRecord> Applicable,
    ImmutableArray<AppliedWaiver> Exceptions,
    ImmutableArray<AuthorityConflict> Conflicts,
    ImmutableArray<RecordRevision> Candidates,
    ImmutableArray<TraceEntry> Trace)
{
    public IEnumerable<AppliedRecord> Effective => Applicable.Where(a => a.IsEffective);

    /// <summary>The most restrictive level demanded by unresolved conflicts, if any (P4).</summary>
    public EnforcementLevel? ConflictSeverity =>
        Conflicts.Where(c => !c.IsResolved).Select(c => (EnforcementLevel?)c.Severity).Max();

    /// <summary>
    /// True when a finding for <paramref name="recordId"/> located at <paramref name="path"/> is waived,
    /// either because the whole record is waived or because an exception covers that path.
    /// </summary>
    public string? WaiverFor(string recordId, EvaluationScope change, string? path)
    {
        var applied = Applicable.FirstOrDefault(a => string.Equals(a.Revision.Id, recordId, StringComparison.Ordinal));
        if (applied is null || applied.Record.IsNonExemptable)
        {
            return null;
        }

        if (!applied.WaivedBy.IsEmpty)
        {
            return applied.WaivedBy[0];
        }

        if (path is null)
        {
            return null;
        }

        return Exceptions
            .Where(e => applied.PartiallyWaivedBy.Contains(e.Revision.Id, StringComparer.Ordinal))
            .OrderBy(e => e.Revision.Id, StringComparer.Ordinal)
            .FirstOrDefault(e => e.Covers(change, path))?.Revision.Id;
    }
}
