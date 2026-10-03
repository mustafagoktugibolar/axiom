using System.Collections.Immutable;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Resolution;

/// <summary>
/// What is known about one scope dimension of a change. <see cref="IsKnown"/> false means the request
/// did not determine the dimension at all, which is different from knowing it is empty.
/// </summary>
public readonly record struct ScopeFacet(bool IsKnown, ImmutableSortedSet<string> Values)
{
    public static ScopeFacet Unknown { get; } = new(false, ImmutableSortedSet<string>.Empty);
}

/// <summary>
/// The scope a change directly touches, resolved from the request and the System Graph. Entity
/// dimensions hold canonical entity references; paths may be concrete files or globs.
/// </summary>
public sealed class EvaluationScope
{
    private readonly ImmutableSortedDictionary<ScopeDimension, ImmutableSortedSet<string>> _known;

    private EvaluationScope(ImmutableSortedDictionary<ScopeDimension, ImmutableSortedSet<string>> known) => _known = known;

    public static EvaluationScope Empty { get; } = new(ImmutableSortedDictionary<ScopeDimension, ImmutableSortedSet<string>>.Empty);

    public ScopeFacet this[ScopeDimension dimension] =>
        _known.TryGetValue(dimension, out var values) ? new ScopeFacet(true, values) : ScopeFacet.Unknown;

    public IEnumerable<ScopeDimension> KnownDimensions => _known.Keys;

    /// <summary>Marks a dimension as known and adds values to it. Passing no values records "known to be none".</summary>
    public EvaluationScope With(ScopeDimension dimension, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var comparer = Scope.ComparerFor(dimension);
        var normalized = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => dimension == ScopeDimension.Path ? GlobPattern.NormalizePath(v) : v.Trim())
            .Where(v => v.Length > 0);
        var existing = _known.TryGetValue(dimension, out var current) ? current : ImmutableSortedSet.Create<string>(comparer);
        return new EvaluationScope(_known.SetItem(dimension, existing.Union(normalized)));
    }

    public EvaluationScope With(ScopeDimension dimension, params string[] values) => With(dimension, (IEnumerable<string>)values);

    public EvaluationScope WithEntities(ScopeDimension dimension, IEnumerable<EntityRef> entities) =>
        With(dimension, entities.Select(e => e.ToString()));

    /// <summary>The same scope narrowed to a single concrete path; used to test per-file waivers.</summary>
    public EvaluationScope NarrowedToPath(string path) =>
        new(_known.SetItem(ScopeDimension.Path, ImmutableSortedSet.Create(Scope.ComparerFor(ScopeDimension.Path), GlobPattern.NormalizePath(path))));

    /// <summary>Stable textual form used in request fingerprints and receipts.</summary>
    public string ToCanonicalString() =>
        string.Join(";", _known.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value)}]"));
}

public enum DimensionOutcome
{
    Unrestricted,
    Matched,
    Indeterminate,
    NotMatched,
}

public sealed record DimensionMatch(ScopeDimension Dimension, DimensionOutcome Outcome, ImmutableArray<string> MatchedValues);

/// <summary>Result of testing whether a governing record's scope intersects a change.</summary>
public sealed record ScopeMatch(bool IsMatch, bool IsPotential, ImmutableArray<DimensionMatch> Dimensions);

public enum Containment
{
    /// <summary>The change is not provably inside the scope.</summary>
    None,

    /// <summary>Every non-path dimension is inside the scope and some, but not all, changed paths are.</summary>
    Partial,

    /// <summary>The whole change is provably inside the scope.</summary>
    Full,
}

public sealed record ContainmentResult(Containment Containment, string Reason);

/// <summary>Deterministic scope matching (R3). No similarity, no heuristics.</summary>
public static class ScopeMatcher
{
    /// <summary>
    /// Tests whether a governing record applies to a change. A dimension the request does not determine
    /// is indeterminate and treated as potentially applicable, so missing information never removes governance.
    /// </summary>
    public static ScopeMatch Match(Scope recordScope, EvaluationScope change)
    {
        ArgumentNullException.ThrowIfNull(recordScope);
        ArgumentNullException.ThrowIfNull(change);

        var dimensions = ImmutableArray.CreateBuilder<DimensionMatch>();
        bool isMatch = true, isPotential = false;

        foreach (var dimension in recordScope.RestrictedDimensions)
        {
            var facet = change[dimension];
            if (!facet.IsKnown)
            {
                dimensions.Add(new DimensionMatch(dimension, DimensionOutcome.Indeterminate, []));
                isPotential = true;
                continue;
            }

            var matched = facet.Values.Where(v => recordScope[dimension].Any(r => Designates(dimension, r, v))).ToImmutableArray();
            if (matched.IsEmpty)
            {
                dimensions.Add(new DimensionMatch(dimension, DimensionOutcome.NotMatched, []));
                isMatch = false;
            }
            else
            {
                dimensions.Add(new DimensionMatch(dimension, DimensionOutcome.Matched, matched));
            }
        }

        return new ScopeMatch(isMatch, isMatch && isPotential, dimensions.ToImmutable());
    }

    /// <summary>
    /// Tests whether a change lies inside a waiving scope (exception). Containment must be provable:
    /// an undetermined dimension, or any changed value outside the scope, yields no containment (P5).
    /// </summary>
    public static ContainmentResult Contain(Scope waivingScope, EvaluationScope change)
    {
        ArgumentNullException.ThrowIfNull(waivingScope);
        ArgumentNullException.ThrowIfNull(change);

        if (waivingScope.IsUnrestricted)
        {
            return new ContainmentResult(Containment.None, "scope is unbounded");
        }

        var partial = false;
        foreach (var dimension in waivingScope.RestrictedDimensions)
        {
            var facet = change[dimension];
            if (!facet.IsKnown)
            {
                return new ContainmentResult(Containment.None, $"{dimension} is not determined by the request");
            }

            if (facet.Values.IsEmpty)
            {
                return new ContainmentResult(Containment.None, $"change has no {dimension}");
            }

            var allowed = waivingScope[dimension];
            var covered = facet.Values.Count(v => allowed.Any(a => Covers(dimension, a, v)));
            if (covered == facet.Values.Count)
            {
                continue;
            }

            if (dimension == ScopeDimension.Path && covered > 0)
            {
                partial = true;
                continue;
            }

            return new ContainmentResult(Containment.None, $"{dimension} reaches outside the scope");
        }

        return partial
            ? new ContainmentResult(Containment.Partial, "only some changed paths are inside the scope")
            : new ContainmentResult(Containment.Full, "change is inside the scope");
    }

    /// <summary>Specificity tuple (design.md §7): 1 where the record restricts the dimension and it matched.</summary>
    public static ImmutableArray<int> Specificity(ScopeMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var matched = match.Dimensions.Where(d => d.Outcome == DimensionOutcome.Matched).Select(d => d.Dimension).ToHashSet();
        int Bit(params ScopeDimension[] dimensions) => dimensions.Any(matched.Contains) ? 1 : 0;

        return
        [
            Bit(ScopeDimension.Component),
            Bit(ScopeDimension.Repository),
            Bit(ScopeDimension.Path),
            Bit(ScopeDimension.Capability),
            Bit(ScopeDimension.Api, ScopeDimension.Resource),
            Bit(ScopeDimension.Environment),
            Bit(ScopeDimension.Technology),
            Bit(ScopeDimension.ChangeClass),
            Bit(ScopeDimension.System),
            Bit(ScopeDimension.Domain),
            Bit(ScopeDimension.Organization),
        ];
    }

    private static bool Designates(ScopeDimension dimension, string recordValue, string changeValue)
    {
        if (dimension == ScopeDimension.Path)
        {
            var pattern = GlobPattern.Parse(recordValue);
            var changed = GlobPattern.Parse(changeValue);
            return changed.HasWildcards ? pattern.MayIntersect(changed) : pattern.IsMatch(changeValue);
        }

        return EntityRef.TryParse(changeValue, out var entity)
            ? entity.IsDesignatedBy(recordValue)
            : string.Equals(recordValue, changeValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Covers(ScopeDimension dimension, string scopeValue, string changeValue) =>
        dimension == ScopeDimension.Path
            ? GlobPattern.Parse(scopeValue).Contains(GlobPattern.Parse(changeValue))
            : Designates(dimension, scopeValue, changeValue);
}
