using System.Collections.Immutable;

namespace Axiom.Domain.Governance;

/// <summary>Dimensions a governance record can be scoped by (requirement R3).</summary>
public enum ScopeDimension
{
    Organization,
    Domain,
    System,
    Component,
    Repository,
    Path,
    Capability,
    Api,
    Resource,
    Technology,
    Environment,
    ChangeClass,
}

/// <summary>
/// Declared scope of a governance record: a conjunction across the dimensions it restricts and a
/// wildcard across the dimensions it omits. Values inside one dimension are alternatives.
/// </summary>
public sealed class Scope : IEquatable<Scope>
{
    private readonly ImmutableSortedDictionary<ScopeDimension, ImmutableSortedSet<string>> _values;

    private Scope(ImmutableSortedDictionary<ScopeDimension, ImmutableSortedSet<string>> values) => _values = values;

    /// <summary>A scope that restricts nothing and therefore applies everywhere.</summary>
    public static Scope Unrestricted { get; } = new(ImmutableSortedDictionary<ScopeDimension, ImmutableSortedSet<string>>.Empty);

    public IEnumerable<ScopeDimension> RestrictedDimensions => _values.Keys;

    public bool IsUnrestricted => _values.IsEmpty;

    public bool Restricts(ScopeDimension dimension) => _values.ContainsKey(dimension);

    public ImmutableSortedSet<string> this[ScopeDimension dimension] =>
        _values.TryGetValue(dimension, out var set) ? set : ImmutableSortedSet<string>.Empty;

    public static Scope Create(IEnumerable<KeyValuePair<ScopeDimension, IEnumerable<string>>> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        var builder = ImmutableSortedDictionary.CreateBuilder<ScopeDimension, ImmutableSortedSet<string>>();
        foreach (var (dimension, raw) in dimensions)
        {
            var comparer = ComparerFor(dimension);
            var values = raw
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => dimension == ScopeDimension.Path ? GlobPattern.Parse(v).Value : v.Trim())
                .ToImmutableSortedSet(comparer);

            // An explicitly empty list restricts nothing; the schema examples use `paths: []` that way.
            if (!values.IsEmpty)
            {
                builder[dimension] = builder.TryGetValue(dimension, out var existing) ? existing.Union(values) : values;
            }
        }

        return builder.Count == 0 ? Unrestricted : new Scope(builder.ToImmutable());
    }

    public static Scope Of(params (ScopeDimension Dimension, string[] Values)[] dimensions) =>
        Create(dimensions.Select(d => new KeyValuePair<ScopeDimension, IEnumerable<string>>(d.Dimension, d.Values)));

    public static StringComparer ComparerFor(ScopeDimension dimension) =>
        dimension == ScopeDimension.Path ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public bool Equals(Scope? other)
    {
        if (other is null || _values.Count != other._values.Count)
        {
            return false;
        }

        foreach (var (dimension, values) in _values)
        {
            if (!other._values.TryGetValue(dimension, out var theirs) || !values.SetEquals(theirs))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as Scope);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var (dimension, values) in _values)
        {
            hash.Add(dimension);
            foreach (var value in values)
            {
                hash.Add(value, ComparerFor(dimension));
            }
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        IsUnrestricted
            ? "*"
            : string.Join("; ", _values.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value)}]"));
}
