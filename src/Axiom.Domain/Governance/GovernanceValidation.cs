using System.Collections.Immutable;

namespace Axiom.Domain.Governance;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>A machine-readable problem found in governance source. Errors prevent snapshot publication.</summary>
public sealed record ValidationIssue(string Code, IssueSeverity Severity, string Message, string? RecordId = null, string? SourcePath = null)
{
    public static ValidationIssue Error(string code, string message, string? recordId = null, string? sourcePath = null) =>
        new(code, IssueSeverity.Error, message, recordId, sourcePath);

    public static ValidationIssue Warning(string code, string message, string? recordId = null, string? sourcePath = null) =>
        new(code, IssueSeverity.Warning, message, recordId, sourcePath);
}

public static class IssueCodes
{
    public const string DuplicateId = "GOV_DUPLICATE_ID";
    public const string MissingOwner = "GOV_MISSING_OWNER";
    public const string IllegalTransition = "GOV_ILLEGAL_LIFECYCLE_TRANSITION";
    public const string UnknownRelationTarget = "GOV_UNKNOWN_RELATION_TARGET";
    public const string SelfRelation = "GOV_SELF_RELATION";
    public const string SupersessionMismatch = "GOV_SUPERSESSION_MISMATCH";
    public const string SupersessionCycle = "GOV_SUPERSESSION_CYCLE";
    public const string SupersededWithoutSuccessor = "GOV_SUPERSEDED_WITHOUT_SUCCESSOR";
    public const string RefinementWeakensHardControl = "GOV_REFINEMENT_WEAKENS_HARD_CONTROL";
    public const string ExceptionTermsMissing = "GOV_EXCEPTION_TERMS_MISSING";
    public const string ExceptionUnknownTarget = "GOV_EXCEPTION_UNKNOWN_TARGET";
    public const string ExceptionTargetsNonExemptable = "GOV_EXCEPTION_TARGETS_NON_EXEMPTABLE";
    public const string ExceptionTargetsException = "GOV_EXCEPTION_TARGETS_EXCEPTION";
    public const string ExceptionScopeTooBroad = "GOV_EXCEPTION_SCOPE_BROADER_THAN_TARGET";
    public const string ExceptionWindowInvalid = "GOV_EXCEPTION_WINDOW_INVALID";
    public const string ExceptionMissingApprover = "GOV_EXCEPTION_MISSING_APPROVER";
    public const string ExceptionSelfApproved = "GOV_EXCEPTION_SELF_APPROVED";
    public const string ValidityWindowInvalid = "GOV_VALIDITY_WINDOW_INVALID";
    public const string CandidateWithoutEvidence = "GOV_CANDIDATE_WITHOUT_EVIDENCE";
    public const string HardControlMustBlock = "GOV_HARD_CONTROL_NOT_ENFORCED";
}

public static class LifecycleRules
{
    private static readonly ImmutableHashSet<(LifecycleStatus From, LifecycleStatus To)> Allowed =
    [
        (LifecycleStatus.Proposed, LifecycleStatus.Accepted),
        (LifecycleStatus.Proposed, LifecycleStatus.Rejected),
        (LifecycleStatus.Accepted, LifecycleStatus.Deprecated),
        (LifecycleStatus.Accepted, LifecycleStatus.Superseded),
        (LifecycleStatus.Deprecated, LifecycleStatus.Superseded),
        (LifecycleStatus.Accepted, LifecycleStatus.Expired),
    ];

    /// <summary>Transitions from docs/03-governance/decision-lifecycle.md. A no-op is always legal.</summary>
    public static bool CanTransition(LifecycleStatus from, LifecycleStatus to) => from == to || Allowed.Contains((from, to));
}

public static class ScopeAlgebra
{
    /// <summary>
    /// True when <paramref name="inner"/> never steps outside <paramref name="outer"/>: on every
    /// dimension both scopes restrict, each inner value is covered by an outer value. Dimensions only
    /// the outer scope restricts are inherited, because a scope that waives or refines a record can only
    /// take effect where that record applies in the first place.
    /// </summary>
    public static bool IsWithin(Scope inner, Scope outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);

        foreach (var dimension in outer.RestrictedDimensions.Where(inner.Restricts))
        {
            var allowed = outer[dimension];
            foreach (var value in inner[dimension])
            {
                var covered = dimension == ScopeDimension.Path
                    ? allowed.Any(a => GlobPattern.Parse(a).Contains(GlobPattern.Parse(value)))
                    : allowed.Contains(value);
                if (!covered)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>Validates one consistent set of governance records (one source commit).</summary>
public static class GovernanceSetValidator
{
    public static ImmutableArray<ValidationIssue> Validate(IReadOnlyCollection<GovernanceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();

        foreach (var duplicate in records.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(ValidationIssue.Error(IssueCodes.DuplicateId, $"Record ID '{duplicate.Key}' is defined {duplicate.Count()} times.", duplicate.Key));
        }

        var byId = records.GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var record in records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ValidateRecord(record, byId, issues);
        }

        ValidateSupersessionCycles(byId, issues);
        return issues.ToImmutable();
    }

    /// <summary>Validates a lifecycle change between two revisions of the same record.</summary>
    public static ValidationIssue? ValidateTransition(GovernanceRecord previous, GovernanceRecord next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        return LifecycleRules.CanTransition(previous.Status, next.Status)
            ? null
            : ValidationIssue.Error(
                IssueCodes.IllegalTransition,
                $"Record '{next.Id}' cannot move from '{previous.Status}' to '{next.Status}'.",
                next.Id);
    }

    private static void ValidateRecord(GovernanceRecord record, Dictionary<string, GovernanceRecord> byId, ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (record.Owners.IsDefaultOrEmpty || record.Owners.All(string.IsNullOrWhiteSpace))
        {
            // R20: every authoritative record has an accountable owner; proposals are flagged, not rejected.
            issues.Add(record.Status == LifecycleStatus.Accepted
                ? ValidationIssue.Error(IssueCodes.MissingOwner, $"Accepted record '{record.Id}' has no accountable owner.", record.Id)
                : ValidationIssue.Warning(IssueCodes.MissingOwner, $"Record '{record.Id}' has no owner.", record.Id));
        }

        if (record.Validity is { EffectiveFrom: { } from, EffectiveUntil: { } until } && until < from)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.ValidityWindowInvalid, $"Record '{record.Id}' ends before it becomes effective.", record.Id));
        }

        if (record.IsNonExemptable && record.Status == LifecycleStatus.Accepted && record.Kind != RecordKind.Exception
            && record.Enforcement.Ceiling < EnforcementLevel.RequireReview)
        {
            issues.Add(ValidationIssue.Warning(
                IssueCodes.HardControlMustBlock,
                $"Non-exemptable record '{record.Id}' never escalates beyond '{record.Enforcement.Ceiling}'.",
                record.Id));
        }

        if (record.Status == LifecycleStatus.Proposed && record.Evidence.IsDefaultOrEmpty && record.Tags.Contains("candidate", StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(ValidationIssue.Error(IssueCodes.CandidateWithoutEvidence, $"Generated candidate '{record.Id}' carries no evidence.", record.Id));
        }

        ValidateRelations(record, byId, issues);

        if (record.Kind == RecordKind.Exception)
        {
            ValidateException(record, byId, issues);
        }
    }

    private static void ValidateRelations(GovernanceRecord record, Dictionary<string, GovernanceRecord> byId, ImmutableArray<ValidationIssue>.Builder issues)
    {
        foreach (var relation in record.Relations)
        {
            if (string.Equals(relation.TargetId, record.Id, StringComparison.Ordinal))
            {
                issues.Add(ValidationIssue.Error(IssueCodes.SelfRelation, $"Record '{record.Id}' declares '{relation.Kind}' on itself.", record.Id));
                continue;
            }

            // motivatedBy points at incidents/issues/reviews outside the registry.
            if (relation.Kind == RelationKind.MotivatedBy)
            {
                continue;
            }

            if (!byId.TryGetValue(relation.TargetId, out var target))
            {
                // `related` may reference records owned by another registry; everything else must resolve.
                issues.Add(relation.Kind == RelationKind.Related
                    ? ValidationIssue.Warning(IssueCodes.UnknownRelationTarget, $"Record '{record.Id}' relates to unknown record '{relation.TargetId}'.", record.Id)
                    : ValidationIssue.Error(IssueCodes.UnknownRelationTarget, $"Record '{record.Id}' declares '{relation.Kind}' on unknown record '{relation.TargetId}'.", record.Id));
                continue;
            }

            if (relation.Kind == RelationKind.Supersedes && record.Status == LifecycleStatus.Accepted
                && target.Status is LifecycleStatus.Accepted or LifecycleStatus.Proposed)
            {
                issues.Add(ValidationIssue.Error(
                    IssueCodes.SupersessionMismatch,
                    $"Accepted record '{record.Id}' supersedes '{target.Id}', which is still '{target.Status}'.",
                    record.Id));
            }

            if (relation.Kind == RelationKind.Refines && target.IsNonExemptable && record.Enforcement.Ceiling < target.Enforcement.Ceiling)
            {
                issues.Add(ValidationIssue.Error(
                    IssueCodes.RefinementWeakensHardControl,
                    $"Record '{record.Id}' refines non-exemptable '{target.Id}' with weaker enforcement.",
                    record.Id));
            }
        }

        if (record.Status == LifecycleStatus.Superseded)
        {
            var named = record.RelatedIds(RelationKind.SupersededBy).Any()
                || byId.Values.Any(r => r.RelatedIds(RelationKind.Supersedes).Contains(record.Id, StringComparer.Ordinal));
            if (!named)
            {
                issues.Add(ValidationIssue.Error(IssueCodes.SupersededWithoutSuccessor, $"Superseded record '{record.Id}' names no successor.", record.Id));
            }
        }
    }

    private static void ValidateException(GovernanceRecord record, Dictionary<string, GovernanceRecord> byId, ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (record.Exception is not { } terms)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.ExceptionTermsMissing, $"Exception '{record.Id}' has no exception terms.", record.Id));
            return;
        }

        if (terms.ExpiresAt <= terms.StartsAt)
        {
            issues.Add(ValidationIssue.Error(IssueCodes.ExceptionWindowInvalid, $"Exception '{record.Id}' expires before it starts.", record.Id));
        }

        if (record.Status == LifecycleStatus.Accepted)
        {
            if (terms.Approvers.IsDefaultOrEmpty)
            {
                issues.Add(ValidationIssue.Error(IssueCodes.ExceptionMissingApprover, $"Accepted exception '{record.Id}' names no approver.", record.Id));
            }
            else if (terms.Approvers.All(a => record.Owners.Contains(a, StringComparer.OrdinalIgnoreCase)))
            {
                issues.Add(ValidationIssue.Warning(IssueCodes.ExceptionSelfApproved, $"Exception '{record.Id}' is approved only by its own owners.", record.Id));
            }
        }

        if (record.Scope.IsUnrestricted)
        {
            // R10 / ADR-0006: an exception names an exact scope; a blanket waiver is never valid.
            issues.Add(ValidationIssue.Error(IssueCodes.ExceptionScopeTooBroad, $"Exception '{record.Id}' declares no scope.", record.Id));
        }

        foreach (var targetId in terms.Targets)
        {
            if (!byId.TryGetValue(targetId, out var target))
            {
                issues.Add(ValidationIssue.Error(IssueCodes.ExceptionUnknownTarget, $"Exception '{record.Id}' targets unknown record '{targetId}'.", record.Id));
                continue;
            }

            if (target.Kind == RecordKind.Exception)
            {
                issues.Add(ValidationIssue.Error(IssueCodes.ExceptionTargetsException, $"Exception '{record.Id}' targets another exception '{targetId}'.", record.Id));
                continue;
            }

            if (target.IsNonExemptable)
            {
                // R11 / P6. The resolver ignores such an exception regardless; this stops it being merged.
                issues.Add(ValidationIssue.Error(IssueCodes.ExceptionTargetsNonExemptable, $"Exception '{record.Id}' targets non-exemptable record '{targetId}'.", record.Id));
            }

            if (!ScopeAlgebra.IsWithin(record.Scope, target.Scope))
            {
                issues.Add(ValidationIssue.Error(IssueCodes.ExceptionScopeTooBroad, $"Exception '{record.Id}' reaches outside the scope of '{targetId}'.", record.Id));
            }
        }
    }

    private static void ValidateSupersessionCycles(Dictionary<string, GovernanceRecord> byId, ImmutableArray<ValidationIssue>.Builder issues)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in byId.Keys.Order(StringComparer.Ordinal))
        {
            if (Visit(id))
            {
                issues.Add(ValidationIssue.Error(IssueCodes.SupersessionCycle, $"Supersession chain through '{id}' is cyclic.", id));
            }
        }

        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var s))
            {
                return s == 1;
            }

            state[id] = 1;
            var cyclic = byId.TryGetValue(id, out var record)
                && record.RelatedIds(RelationKind.Supersedes).Order(StringComparer.Ordinal).Any(Visit);
            state[id] = 2;
            return cyclic;
        }
    }
}
