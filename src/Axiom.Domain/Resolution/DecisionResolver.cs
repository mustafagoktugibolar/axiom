using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Resolution;

/// <summary>
/// Inputs of one resolution. <see cref="HardGate"/> is true when the caller needs a single answer
/// (for example a merge gate), which turns unresolved conflicts into <c>BLOCK</c> (design.md §6).
/// </summary>
public sealed record ResolutionInput(
    IReadOnlyCollection<RecordRevision> Records,
    EvaluationScope Change,
    DateTimeOffset At,
    bool HardGate);

/// <summary>
/// The deterministic decision resolver (design.md §4.4, docs/02-architecture/decision-resolution-engine.md).
/// A pure function: identical inputs always produce an identical result (P3).
/// </summary>
public static class DecisionResolver
{
    public static ResolutionResult Resolve(ResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var today = DateOnly.FromDateTime(input.At.UtcDateTime);
        var trace = new SortedDictionary<string, TraceEntry>(StringComparer.Ordinal);
        var working = new SortedDictionary<string, Working>(StringComparer.Ordinal);
        var candidates = ImmutableArray.CreateBuilder<RecordRevision>();
        var ordered = input.Records.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();

        // Steps 3: lifecycle filter, then structural scope selection.
        foreach (var revision in ordered.Where(r => r.Record.Kind != RecordKind.Exception))
        {
            var record = revision.Record;
            var match = ScopeMatcher.Match(record.Scope, input.Change);
            var specificity = ScopeMatcher.Specificity(match);

            if (!record.IsAuthoritativeOn(today))
            {
                if (record.Status == LifecycleStatus.Proposed && match.IsMatch)
                {
                    candidates.Add(revision);
                }

                trace[revision.Id] = Entry(revision, false, [LifecycleReason(record, today)], specificity);
                continue;
            }

            var reasons = ImmutableArray.CreateBuilder<string>();
            reasons.Add($"status={Lower(record.Status)}");
            reasons.AddRange(ScopeReasons(record.Scope, match));

            if (!match.IsMatch)
            {
                trace[revision.Id] = Entry(revision, false, reasons.ToImmutable(), specificity);
                continue;
            }

            working[revision.Id] = new Working(revision, RankOf(record), match.IsPotential, specificity, reasons);
        }

        // Step 4: exceptions.
        var exceptions = ImmutableArray.CreateBuilder<AppliedWaiver>();
        foreach (var revision in ordered.Where(r => r.Record.Kind == RecordKind.Exception))
        {
            exceptions.AddIfNotNull(ApplyException(revision, input, working, trace));
        }

        // Step 5: legal refinements.
        foreach (var refining in working.Values.ToArray())
        {
            foreach (var targetId in refining.Revision.Record.RelatedIds(RelationKind.Refines).Order(StringComparer.Ordinal))
            {
                if (!working.TryGetValue(targetId, out var target))
                {
                    continue;
                }

                if (target.Revision.Record.IsNonExemptable)
                {
                    refining.Reasons.Add($"refines {targetId}, which is non-exemptable and stays in force");
                }
                else if (!ScopeAlgebra.IsWithin(refining.Revision.Record.Scope, target.Revision.Record.Scope))
                {
                    refining.Reasons.Add($"declares refinement of {targetId} but is not within its scope");
                }
                else if (refining.WaivedBy.Count == 0)
                {
                    target.RefinedBy.Add(refining.Revision.Id);
                    target.Reasons.Add($"refined by more specific {refining.Revision.Id}");
                }
            }
        }

        // Step 6: authoritative conflicts among records still in force.
        var conflicts = DetectConflicts(working, input.HardGate);

        var applicable = working.Values
            .Select(w => w.ToApplied())
            .OrderBy(a => (int)a.Rank)
            .ThenBy(a => (int)a.Record.Authority.Level)
            .ThenByDescending(a => SpecificityKey(a.Specificity), StringComparer.Ordinal)
            .ThenBy(a => a.Revision.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var applied in applicable)
        {
            trace[applied.Revision.Id] = Entry(applied.Revision, true, applied.Reasons, applied.Specificity);
        }

        return new ResolutionResult(applicable, exceptions.ToImmutable(), conflicts, candidates.ToImmutable(), [.. trace.Values]);
    }

    private static AppliedWaiver? ApplyException(
        RecordRevision revision,
        ResolutionInput input,
        SortedDictionary<string, Working> working,
        SortedDictionary<string, TraceEntry> trace)
    {
        var record = revision.Record;
        var reasons = ImmutableArray.CreateBuilder<string>();
        var none = ImmutableArray<int>.Empty;

        if (record.Status != LifecycleStatus.Accepted || record.Exception is not { } terms)
        {
            trace[revision.Id] = Entry(revision, false, [$"status={Lower(record.Status)}; an exception waives only once approved"], none);
            return null;
        }

        if (!terms.IsActiveAt(input.At))
        {
            var why = input.At >= terms.ExpiresAt ? $"expired at {terms.ExpiresAt:O}" : $"not active until {terms.StartsAt:O}";
            trace[revision.Id] = Entry(revision, false, [why], none);
            return null;
        }

        var containment = ScopeMatcher.Contain(record.Scope, input.Change);
        if (containment.Containment == Containment.None)
        {
            trace[revision.Id] = Entry(revision, false, [$"does not cover the change: {containment.Reason}"], none);
            return null;
        }

        var waived = ImmutableArray.CreateBuilder<string>();
        foreach (var targetId in terms.Targets.Order(StringComparer.Ordinal))
        {
            if (!working.TryGetValue(targetId, out var target))
            {
                reasons.Add($"target {targetId} does not apply to the change");
            }
            else if (target.Revision.Record.IsNonExemptable)
            {
                reasons.Add($"target {targetId} is non-exemptable and cannot be waived");
                target.Reasons.Add($"exception {revision.Id} ignored: record is non-exemptable");
            }
            else if (containment.Containment == Containment.Full)
            {
                target.WaivedBy.Add(revision.Id);
                target.Reasons.Add($"waived by {revision.Id} until {terms.ExpiresAt:O}");
                waived.Add(targetId);
            }
            else
            {
                target.PartiallyWaivedBy.Add(revision.Id);
                target.Reasons.Add($"waived by {revision.Id} for the paths it covers");
                waived.Add(targetId);
            }
        }

        if (waived.Count == 0)
        {
            trace[revision.Id] = Entry(revision, false, reasons.ToImmutable(), none);
            return null;
        }

        reasons.Insert(0, $"active until {terms.ExpiresAt:O}; {containment.Reason}");
        trace[revision.Id] = Entry(revision, true, reasons.ToImmutable(), none);
        return new AppliedWaiver(revision, containment.Containment, waived.ToImmutable());
    }

    private static ImmutableArray<AuthorityConflict> DetectConflicts(SortedDictionary<string, Working> working, bool hardGate)
    {
        var inForce = working.Values.Where(w => w.WaivedBy.Count == 0 && w.RefinedBy.Count == 0).ToArray();
        var conflicts = ImmutableArray.CreateBuilder<AuthorityConflict>();
        var overrides = new List<(Working Loser, string Winner)>();

        for (var i = 0; i < inForce.Length; i++)
        {
            for (var j = i + 1; j < inForce.Length; j++)
            {
                var (a, b) = (inForce[i], inForce[j]);
                if (FindConflict(a.Revision.Record, b.Revision.Record) is not var (source, explanation))
                {
                    continue;
                }

                var bothHard = a.Revision.Record.IsNonExemptable && b.Revision.Record.IsNonExemptable;
                if (bothHard)
                {
                    conflicts.Add(new AuthorityConflict(ConflictCodes.ConfigurationError, EnforcementLevel.Block, a.Revision.Id, b.Revision.Id, source, null,
                        $"Two non-exemptable controls contradict each other: {explanation}"));
                }
                else if (a.Rank != b.Rank)
                {
                    var (winner, loser) = a.Rank < b.Rank ? (a, b) : (b, a);
                    overrides.Add((loser, winner.Revision.Id));
                    conflicts.Add(new AuthorityConflict(ConflictCodes.ResolvedByPrecedence, EnforcementLevel.Info, a.Revision.Id, b.Revision.Id, source, winner.Revision.Id,
                        $"{winner.Revision.Id} ({winner.Rank}) prevails over {loser.Revision.Id} ({loser.Rank}): {explanation}"));
                }
                else
                {
                    conflicts.Add(new AuthorityConflict(ConflictCodes.Unresolved, hardGate ? EnforcementLevel.Block : EnforcementLevel.RequireReview,
                        a.Revision.Id, b.Revision.Id, source, null,
                        $"Both records are authoritative at the same precedence and neither refines the other: {explanation}"));
                }
            }
        }

        foreach (var (loser, winner) in overrides)
        {
            loser.OverriddenBy.Add(winner);
            loser.Reasons.Add($"overridden by {winner}, which has higher precedence");
        }

        return conflicts.ToImmutable();
    }

    private static (ConflictSource Source, string Explanation)? FindConflict(GovernanceRecord a, GovernanceRecord b)
    {
        if (a.RelatedIds(RelationKind.ConflictsWith).Contains(b.Id, StringComparer.Ordinal)
            || b.RelatedIds(RelationKind.ConflictsWith).Contains(a.Id, StringComparer.Ordinal))
        {
            return (ConflictSource.Explicit, $"{a.Id} and {b.Id} are declared to conflict");
        }

        var contradiction = Contradiction(a, b) ?? Contradiction(b, a);
        return contradiction is null ? null : (ConflictSource.Structural, contradiction);
    }

    private static string? Contradiction(GovernanceRecord forbids, GovernanceRecord prefers)
    {
        var preferred = prefers.Preferred.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var clash = forbids.Forbidden.Select(Normalize).Order(StringComparer.Ordinal).FirstOrDefault(preferred.Contains);
        return clash is null ? null : $"{forbids.Id} forbids what {prefers.Id} prefers (\"{clash}\")";
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.').ToLowerInvariant();

    private static PrecedenceRank RankOf(GovernanceRecord record)
    {
        if (record.IsNonExemptable)
        {
            return PrecedenceRank.HardControl;
        }

        if (record.Authority.Level == AuthorityLevel.Advisory || record.Enforcement.Ceiling == EnforcementLevel.Info)
        {
            return PrecedenceRank.Advisory;
        }

        return record.Kind is RecordKind.Principle or RecordKind.Goal ? PrecedenceRank.PrincipleOrGoal : PrecedenceRank.AcceptedRecord;
    }

    private static string LifecycleReason(GovernanceRecord record, DateOnly today)
    {
        if (record.Status is LifecycleStatus.Accepted or LifecycleStatus.Deprecated && !record.Validity.IsEffectiveOn(today))
        {
            return record.Validity.EffectiveFrom is { } from && today < from
                ? $"status={Lower(record.Status)} but not effective until {from:O}"
                : $"status={Lower(record.Status)} but no longer effective since {record.Validity.EffectiveUntil:O}";
        }

        return $"status={Lower(record.Status)}; only accepted records govern";
    }

    private static IEnumerable<string> ScopeReasons(Scope scope, ScopeMatch match)
    {
        if (scope.IsUnrestricted)
        {
            yield return "scope is unrestricted";
        }

        foreach (var dimension in match.Dimensions)
        {
            var name = Lower(dimension.Dimension);
            yield return dimension.Outcome switch
            {
                DimensionOutcome.Matched => $"{name}={string.Join(",", dimension.MatchedValues)} matched",
                DimensionOutcome.Indeterminate => $"{name} not determined by the request; assumed applicable",
                _ => $"{name} did not match [{string.Join(",", scope[dimension.Dimension])}]",
            };
        }
    }

    private static string SpecificityKey(ImmutableArray<int> specificity) => string.Concat(specificity);

    private static string Lower<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static TraceEntry Entry(RecordRevision revision, bool selected, ImmutableArray<string> reasons, ImmutableArray<int> specificity) =>
        new(revision.Id, revision.Revision, revision.Record.Kind, selected, reasons, specificity);

    private static void AddIfNotNull<T>(this ImmutableArray<T>.Builder builder, T? item)
        where T : class
    {
        if (item is not null)
        {
            builder.Add(item);
        }
    }

    private sealed class Working(RecordRevision revision, PrecedenceRank rank, bool isPotential, ImmutableArray<int> specificity, ImmutableArray<string>.Builder reasons)
    {
        public RecordRevision Revision { get; } = revision;

        public PrecedenceRank Rank { get; } = rank;

        public ImmutableArray<string>.Builder Reasons { get; } = reasons;

        public SortedSet<string> WaivedBy { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> PartiallyWaivedBy { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> RefinedBy { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> OverriddenBy { get; } = new(StringComparer.Ordinal);

        public AppliedRecord ToApplied() => new(
            Revision, Rank, isPotential, specificity,
            [.. WaivedBy], [.. PartiallyWaivedBy], [.. RefinedBy], [.. OverriddenBy], Reasons.ToImmutable());
    }
}
