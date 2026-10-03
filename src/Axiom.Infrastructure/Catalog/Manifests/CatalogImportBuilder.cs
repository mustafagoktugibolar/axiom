using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Axiom.Infrastructure.Catalog.Manifests;

/// <summary>Collects de-duplicated entities and edges observed from one declared source.</summary>
internal sealed class CatalogImportBuilder(ProvenanceSource source, string sourceLocator, DateTimeOffset observedAt)
{
    private readonly Dictionary<EntityRef, SoftwareEntity> _entities = [];
    private readonly HashSet<EntityRef> _declared = [];
    private readonly Dictionary<(EntityRef From, RelationType Relation, EntityRef To), SoftwareEdge> _edges = [];

    private EntityProvenance Provenance => EntityProvenance.Declared(source, sourceLocator, observedAt);

    /// <summary>Adds a fully described entity. False when the same entity was already described in this source.</summary>
    public bool AddEntity(EntityRef reference, string title, string? description, IEnumerable<string> technologies, IEnumerable<KeyValuePair<string, string>> attributes)
    {
        if (!_declared.Add(reference))
        {
            return false;
        }

        _entities[reference] = Create(reference, title, description, technologies, attributes);
        return true;
    }

    /// <summary>
    /// Adds an entity that is declared only by being referenced (a component naming its repository).
    /// A full description of the same entity in this source takes precedence.
    /// </summary>
    public void AddReferenced(EntityRef reference, IEnumerable<KeyValuePair<string, string>> attributes)
    {
        if (!_entities.ContainsKey(reference))
        {
            _entities[reference] = Create(reference, reference.SimpleName, null, [], attributes);
        }
    }

    public void AddEdge(EntityRef from, RelationType relation, EntityRef target) =>
        _edges[(from, relation, target)] = new SoftwareEdge(from, relation, target, Provenance);

    public CatalogImport Build() => new(
        source,
        sourceLocator,
        [.. _entities.Values.OrderBy(e => e.Ref)],
        [.. _edges.Values.OrderBy(e => e.From).ThenBy(e => e.Relation).ThenBy(e => e.To)]);

    private SoftwareEntity Create(EntityRef reference, string title, string? description, IEnumerable<string> technologies, IEnumerable<KeyValuePair<string, string>> attributes) =>
        new(
            reference,
            title,
            description,
            [.. technologies.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            attributes.ToImmutableSortedDictionary(StringComparer.Ordinal),
            Provenance);
}

/// <summary>Loads every document of a YAML stream as JSON with all scalars kept as strings.</summary>
internal static class YamlDocuments
{
    public const int MaxContentLength = 1_048_576;
    private const int MaxNodes = 100_000;

    /// <exception cref="YamlException">The text is not valid YAML or is too large.</exception>
    public static IReadOnlyList<JsonNode?> Load(string content)
    {
        if (content.Length > MaxContentLength)
        {
            throw new YamlException($"Manifest exceeds {MaxContentLength} characters.");
        }

        var stream = new YamlStream();
        using var reader = new StringReader(content);
        stream.Load(reader);
        var budget = MaxNodes;
        return [.. stream.Documents.Select(d => Convert(d.RootNode, ref budget))];
    }

    private static JsonNode? Convert(YamlNode node, ref int budget)
    {
        // Anchors are resolved by reference, so a small file can expand enormously; the budget bounds it.
        if (--budget < 0)
        {
            throw new YamlException("Manifest is too large or expands too far through anchors.");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                var result = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        throw new YamlException($"Only scalar keys are supported (at {key.Start}).");
                    }

                    if (result.ContainsKey(name))
                    {
                        throw new YamlException($"Duplicate key '{name}' at {key.Start}.");
                    }

                    result[name] = Convert(value, ref budget);
                }

                return result;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var child in sequence.Children)
                {
                    array.Add(Convert(child, ref budget));
                }

                return array;
            case YamlScalarNode scalar:
                var isNull = scalar.Style == ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null";
                return isNull ? null : JsonValue.Create(scalar.Value);
            default:
                throw new YamlException($"Unsupported YAML node at {node.Start}.");
        }
    }
}

/// <summary>Lenient, error-collecting accessors over a JSON object.</summary>
internal static class JsonFields
{
    public static JsonObject? Object(JsonObject parent, string key, string owner, List<string> errors)
    {
        switch (parent[key])
        {
            case null:
                return null;
            case JsonObject value:
                return value;
            default:
                errors.Add($"{owner}{key} must be a mapping.");
                return null;
        }
    }

    public static string? String(JsonObject parent, string key, string owner, List<string> errors)
    {
        switch (parent[key])
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            default:
                errors.Add($"{owner}{key} must be a string.");
                return null;
        }
    }

    public static IReadOnlyList<string> Strings(JsonObject parent, string key, string owner, List<string> errors)
    {
        switch (parent[key])
        {
            case null:
                return [];
            case JsonArray array:
                var values = new List<string>();
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    {
                        values.Add(text.Trim());
                    }
                    else
                    {
                        errors.Add($"{owner}{key} must contain only non-empty strings.");
                    }
                }

                return values;
            default:
                errors.Add($"{owner}{key} must be a list.");
                return [];
        }
    }
}
