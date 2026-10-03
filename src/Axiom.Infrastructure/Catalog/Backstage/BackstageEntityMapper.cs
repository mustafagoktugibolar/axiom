using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Manifests;

namespace Axiom.Infrastructure.Catalog.Backstage;

/// <summary>
/// Maps one Backstage entity (as served by the catalog API or written in <c>catalog-info.yaml</c>) to
/// graph entities and edges. Domain, System, Component, API, Resource and Group are mapped; other
/// kinds (User, Location, Template, …) have no counterpart in the System Graph and are skipped.
/// </summary>
internal static class BackstageEntityMapper
{
    public const string ApiVersionPrefix = "backstage.io/";

    /// <summary>Annotation through which a Backstage entity may declare its technologies to Axiom.</summary>
    public const string TechnologiesAnnotation = "axiom.dev/technologies";

    private const string DefaultNamespace = "default";

    private static readonly ImmutableDictionary<string, EntityKind> Kinds = new Dictionary<string, EntityKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["domain"] = EntityKind.Domain,
        ["system"] = EntityKind.System,
        ["component"] = EntityKind.Component,
        ["api"] = EntityKind.Api,
        ["resource"] = EntityKind.Resource,
        ["group"] = EntityKind.Team,
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source-control hosts whose project-slug annotation identifies the entity's repository.</summary>
    private static readonly ImmutableArray<(string Annotation, string Host)> ProjectSlugs =
        [("github.com/project-slug", "github.com"), ("gitlab.com/project-slug", "gitlab.com")];

    public static bool IsBackstage(JsonObject document) =>
        document["apiVersion"] is JsonValue value && value.TryGetValue<string>(out var apiVersion)
        && apiVersion.StartsWith(ApiVersionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds the entity to <paramref name="builder"/>. Problems are appended to <paramref name="errors"/>.</summary>
    public static void Map(JsonObject entity, CatalogImportBuilder builder, List<string> errors)
    {
        var kindName = JsonFields.String(entity, "kind", string.Empty, errors);
        if (kindName is null)
        {
            errors.Add("kind is required.");
            return;
        }

        if (!Kinds.TryGetValue(kindName, out var kind))
        {
            return;
        }

        var metadata = JsonFields.Object(entity, "metadata", string.Empty, errors);
        var name = metadata is null ? null : JsonFields.String(metadata, "name", "metadata.", errors);
        if (metadata is null || name is null)
        {
            errors.Add("metadata.name is required.");
            return;
        }

        var entityNamespace = JsonFields.String(metadata, "namespace", "metadata.", errors) ?? DefaultNamespace;
        if (!TryCreate(kind, entityNamespace, name, out var self))
        {
            errors.Add($"'{name}' is not a valid entity name.");
            return;
        }

        var spec = JsonFields.Object(entity, "spec", string.Empty, errors) ?? [];
        var annotations = JsonFields.Object(metadata, "annotations", "metadata.", errors) ?? [];

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddAttribute(attributes, "type", JsonFields.String(spec, "type", "spec.", errors));
        AddAttribute(attributes, "lifecycle", JsonFields.String(spec, "lifecycle", "spec.", errors));
        AddAttribute(attributes, "namespace", entityNamespace == DefaultNamespace ? null : entityNamespace);
        var tags = JsonFields.Strings(metadata, "tags", "metadata.", errors);
        AddAttribute(attributes, "tags", tags.Count == 0 ? null : string.Join(' ', tags));

        var title = JsonFields.String(metadata, "title", "metadata.", errors);
        if (title is null && kind == EntityKind.Team && JsonFields.Object(spec, "profile", "spec.", errors) is { } profile)
        {
            title = JsonFields.String(profile, "displayName", "spec.profile.", errors);
        }

        if (!builder.AddEntity(
                self,
                title ?? name,
                JsonFields.String(metadata, "description", "metadata.", errors),
                RepositoryAttributes.SplitList(JsonFields.String(annotations, TechnologiesAnnotation, "metadata.annotations.", errors)),
                attributes))
        {
            errors.Add($"{self} is declared more than once.");
            return;
        }

        if (entity["relations"] is JsonArray relations)
        {
            MapRelations(self, relations, builder, errors);
        }
        else
        {
            MapSpec(self, entityNamespace, spec, builder, errors);
        }

        if (kind == EntityKind.Component)
        {
            MapRepository(self, annotations, builder, errors);
        }
    }

    /// <summary>Parses a Backstage reference <c>[kind:][namespace/]name</c>. False for kinds the graph does not model.</summary>
    internal static bool TryParseRef(string value, EntityKind? defaultKind, string defaultNamespace, out EntityRef reference)
    {
        reference = default;
        var text = value.Trim();
        EntityKind kind;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (!Kinds.TryGetValue(text[..colon], out kind))
            {
                return false;
            }

            text = text[(colon + 1)..];
        }
        else if (defaultKind is { } fallback)
        {
            kind = fallback;
        }
        else
        {
            return false;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return slash < 0
            ? TryCreate(kind, defaultNamespace, text, out reference)
            : TryCreate(kind, text[..slash], text[(slash + 1)..], out reference);
    }

    private static bool TryCreate(EntityKind kind, string entityNamespace, string name, out EntityRef reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            reference = new EntityRef(kind, string.Equals(entityNamespace, DefaultNamespace, StringComparison.OrdinalIgnoreCase) ? name : $"{entityNamespace}/{name}");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void MapSpec(EntityRef self, string entityNamespace, JsonObject spec, CatalogImportBuilder builder, List<string> errors)
    {
        void One(string field, EntityKind defaultKind, Action<EntityRef> add)
        {
            if (JsonFields.String(spec, field, "spec.", errors) is { } value)
            {
                Resolve(field, value, defaultKind, add);
            }
        }

        void Many(string field, EntityKind? defaultKind, Action<EntityRef> add)
        {
            foreach (var value in JsonFields.Strings(spec, field, "spec.", errors))
            {
                Resolve(field, value, defaultKind, add);
            }
        }

        void Resolve(string field, string value, EntityKind? defaultKind, Action<EntityRef> add)
        {
            if (TryParseRef(value, defaultKind, entityNamespace, out var other))
            {
                add(other);
            }
            else if (!IsUnmodelledKind(value))
            {
                errors.Add($"spec.{field}: '{value}' is not a valid entity reference.");
            }
        }

        One("owner", EntityKind.Team, owner => builder.AddEdge(owner, RelationType.Owns, self));
        One("system", EntityKind.System, system => builder.AddEdge(system, RelationType.Contains, self));
        One("domain", EntityKind.Domain, domain => builder.AddEdge(domain, RelationType.Contains, self));
        One("subcomponentOf", EntityKind.Component, parent => builder.AddEdge(parent, RelationType.Contains, self));
        One("subdomainOf", EntityKind.Domain, parent => builder.AddEdge(parent, RelationType.Contains, self));
        Many("providesApis", EntityKind.Api, api => builder.AddEdge(self, RelationType.Provides, api));
        Many("consumesApis", EntityKind.Api, api => builder.AddEdge(self, RelationType.Consumes, api));
        Many("dependsOn", EntityKind.Component, dependency => builder.AddEdge(self, RelationType.DependsOn, dependency));
        Many("dependencyOf", EntityKind.Component, dependent => builder.AddEdge(dependent, RelationType.DependsOn, self));
    }

    private static void MapRelations(EntityRef self, JsonArray relations, CatalogImportBuilder builder, List<string> errors)
    {
        foreach (var item in relations)
        {
            if (item is not JsonObject relation
                || JsonFields.String(relation, "type", "relations[].", errors) is not { } type
                || JsonFields.String(relation, "targetRef", "relations[].", errors) is not { } targetRef)
            {
                errors.Add($"{self}: a relation is missing type or targetRef.");
                continue;
            }

            if (!TryParseRef(targetRef, null, DefaultNamespace, out var other))
            {
                continue;
            }

            switch (type)
            {
                case "ownedBy": builder.AddEdge(other, RelationType.Owns, self); break;
                case "ownerOf": builder.AddEdge(self, RelationType.Owns, other); break;
                case "partOf": builder.AddEdge(other, RelationType.Contains, self); break;
                case "hasPart": builder.AddEdge(self, RelationType.Contains, other); break;
                case "providesApi": builder.AddEdge(self, RelationType.Provides, other); break;
                case "apiProvidedBy": builder.AddEdge(other, RelationType.Provides, self); break;
                case "consumesApi": builder.AddEdge(self, RelationType.Consumes, other); break;
                case "apiConsumedBy": builder.AddEdge(other, RelationType.Consumes, self); break;
                case "dependsOn": builder.AddEdge(self, RelationType.DependsOn, other); break;
                case "dependencyOf": builder.AddEdge(other, RelationType.DependsOn, self); break;
                default: break; // childOf, memberOf, … describe the org chart, not the software graph.
            }
        }
    }

    private static void MapRepository(EntityRef component, JsonObject annotations, CatalogImportBuilder builder, List<string> errors)
    {
        foreach (var (annotation, host) in ProjectSlugs)
        {
            if (JsonFields.String(annotations, annotation, "metadata.annotations.", errors) is not { } slug)
            {
                continue;
            }

            EntityRef repository;
            try
            {
                repository = new EntityRef(EntityKind.Repository, slug);
            }
            catch (FormatException)
            {
                errors.Add($"metadata.annotations.{annotation}: '{slug}' is not a valid project slug.");
                continue;
            }

            builder.AddReferenced(repository, [new(RepositoryAttributes.CloneUrls, $"https://{host}/{slug}")]);
            builder.AddEdge(repository, RelationType.Implements, component);
        }
    }

    /// <summary>A syntactically valid reference to a kind the graph does not model, such as <c>user:jane</c>.</summary>
    private static bool IsUnmodelledKind(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon < value.Length - 1 && !Kinds.ContainsKey(value[..colon].Trim());
    }

    private static void AddAttribute(Dictionary<string, string> attributes, string key, string? value)
    {
        if (value is not null)
        {
            attributes[key] = value;
        }
    }
}
