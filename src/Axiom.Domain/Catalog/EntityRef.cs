using System.Collections.Immutable;

namespace Axiom.Domain.Catalog;

/// <summary>Canonical entity kinds of the System Graph (docs/02-architecture/system-graph.md).</summary>
public enum EntityKind
{
    Organization,
    Domain,
    System,
    Component,
    Repository,
    Api,
    Resource,
    Database,
    Queue,
    EventStream,
    DataProduct,
    Deployment,
    Capability,
    Team,
    Environment,
}

/// <summary>
/// Stable identifier of a software entity, written <c>kind:name</c> where the name may be namespaced
/// with <c>/</c>, for example <c>component:gui/api-gateway</c> or <c>repo:gateway</c> (task 0.7).
/// </summary>
public readonly record struct EntityRef : IComparable<EntityRef>
{
    private static readonly ImmutableDictionary<EntityKind, string> Prefixes = new Dictionary<EntityKind, string>
    {
        [EntityKind.Organization] = "org",
        [EntityKind.Domain] = "domain",
        [EntityKind.System] = "system",
        [EntityKind.Component] = "component",
        [EntityKind.Repository] = "repo",
        [EntityKind.Api] = "api",
        [EntityKind.Resource] = "resource",
        [EntityKind.Database] = "database",
        [EntityKind.Queue] = "queue",
        [EntityKind.EventStream] = "stream",
        [EntityKind.DataProduct] = "dataproduct",
        [EntityKind.Deployment] = "deployment",
        [EntityKind.Capability] = "capability",
        [EntityKind.Team] = "team",
        [EntityKind.Environment] = "environment",
    }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<string, EntityKind> Kinds =
        Prefixes.ToImmutableDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public EntityRef(EntityKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim().ToLowerInvariant();
        if (normalized.Contains(':', StringComparison.Ordinal) || normalized.Any(char.IsWhiteSpace)
            || normalized.StartsWith('/') || normalized.EndsWith('/'))
        {
            throw new FormatException($"'{name}' is not a valid entity name.");
        }

        Kind = kind;
        Name = normalized;
    }

    public EntityKind Kind { get; }

    /// <summary>Lower-cased, possibly namespaced name, e.g. <c>gui/api-gateway</c>.</summary>
    public string Name { get; }

    /// <summary>Last name segment, e.g. <c>api-gateway</c>.</summary>
    public string SimpleName => Name[(Name.LastIndexOf('/') + 1)..];

    public static string PrefixOf(EntityKind kind) => Prefixes[kind];

    public static EntityRef Parse(string value) =>
        TryParse(value, out var reference) ? reference : throw new FormatException($"'{value}' is not a valid entity reference (expected kind:name).");

    public static bool TryParse(string? value, out EntityRef reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 || !Kinds.TryGetValue(value[..separator].Trim(), out var kind))
        {
            return false;
        }

        try
        {
            reference = new EntityRef(kind, value[(separator + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a scope value written in a governance record designates this entity. Records may use
    /// the full reference, the namespaced name, or the simple name.
    /// </summary>
    public bool IsDesignatedBy(string scopeValue)
    {
        var value = scopeValue.Trim();
        return string.Equals(value, ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, SimpleName, StringComparison.OrdinalIgnoreCase);
    }

    public int CompareTo(EntityRef other)
    {
        var byKind = Kind.CompareTo(other.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(Name, other.Name);
    }

    public override string ToString() => Name is null ? string.Empty : $"{Prefixes[Kind]}:{Name}";

    public static bool operator <(EntityRef left, EntityRef right) => left.CompareTo(right) < 0;

    public static bool operator >(EntityRef left, EntityRef right) => left.CompareTo(right) > 0;

    public static bool operator <=(EntityRef left, EntityRef right) => left.CompareTo(right) <= 0;

    public static bool operator >=(EntityRef left, EntityRef right) => left.CompareTo(right) >= 0;
}
