using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Backstage;
using YamlDotNet.Core;

namespace Axiom.Infrastructure.Catalog.Manifests;

/// <summary>
/// Parses repository catalog manifests: Axiom's own <c>.axiom/catalog.yaml</c> (domain-model.md,
/// "Software graph entity") and Backstage's <c>catalog-info.yaml</c>. A file may hold several YAML
/// documents and may mix both shapes. Any error rejects the whole file, so a half-understood manifest
/// can never retract facts a previous import of the same file established.
/// </summary>
public sealed class CatalogManifestParser : ICatalogManifestParser
{
    private static readonly ImmutableDictionary<string, EntityKind> Kinds = BuildKinds();

    private static readonly ImmutableHashSet<string> SpecFields = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "system", "domain", "owner", "repositories", "providesApis", "consumesApis", "dependsOn", "capabilities",
        "technologies", "resources", "storesIn", "publishesTo", "subscribesTo", "deployments",
        "cloneUrl", "cloneUrls", "aliases", "components", "type", "lifecycle");

    public IReadOnlyList<string> ManifestPaths { get; } = [".axiom/catalog.yaml", ".axiom/catalog.yml", "catalog-info.yaml"];

    public CatalogManifestResult Parse(string organizationId, string sourceLocator, string path, string content, DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLocator);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        IReadOnlyList<JsonNode?> documents;
        try
        {
            documents = YamlDocuments.Load(content);
        }
        catch (YamlException exception)
        {
            return new CatalogManifestResult(null, [$"{path}: not valid YAML: {exception.Message}"]);
        }

        // Each manifest file is its own source, so two manifests of one repository do not retract each other.
        var builder = new CatalogImportBuilder(ProvenanceSource.RepositoryManifest, $"{sourceLocator.Trim()}#{path.Trim()}", observedAt);
        var errors = new List<string>();
        for (var index = 0; index < documents.Count; index++)
        {
            var documentErrors = new List<string>();
            switch (documents[index])
            {
                case null:
                    continue;
                case JsonObject document when BackstageEntityMapper.IsBackstage(document):
                    BackstageEntityMapper.Map(document, builder, documentErrors);
                    break;
                case JsonObject document:
                    MapAxiom(document, builder, documentErrors);
                    break;
                default:
                    documentErrors.Add("a catalog document must be a mapping.");
                    break;
            }

            errors.AddRange(documentErrors.Select(error => $"{path}: document {index + 1}: {error}"));
        }

        return errors.Count > 0
            ? new CatalogManifestResult(null, [.. errors])
            : new CatalogManifestResult(builder.Build(), []);
    }

    private static void MapAxiom(JsonObject document, CatalogImportBuilder builder, List<string> errors)
    {
        var kindName = JsonFields.String(document, "kind", string.Empty, errors);
        if (kindName is null)
        {
            errors.Add("kind is required.");
            return;
        }

        if (!Kinds.TryGetValue(kindName, out var kind))
        {
            errors.Add($"unknown kind '{kindName}'.");
            return;
        }

        var metadata = JsonFields.Object(document, "metadata", string.Empty, errors) ?? [];
        var id = JsonFields.String(metadata, "id", "metadata.", errors);
        var name = JsonFields.String(metadata, "name", "metadata.", errors);
        EntityRef self;
        if (id is not null)
        {
            if (!EntityRef.TryParse(id, out self))
            {
                errors.Add($"metadata.id '{id}' is not a valid entity reference (expected kind:name).");
                return;
            }

            if (self.Kind != kind)
            {
                errors.Add($"metadata.id '{id}' does not match kind '{kindName}'.");
                return;
            }
        }
        else if (name is null || !TryRef(name, kind, out self))
        {
            errors.Add("metadata.id (kind:name) or a valid metadata.name is required.");
            return;
        }

        var spec = JsonFields.Object(document, "spec", string.Empty, errors) ?? [];
        foreach (var unknown in spec.Select(p => p.Key).Where(k => !SpecFields.Contains(k)))
        {
            errors.Add($"spec.{unknown} is not a known field.");
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in new[] { "type", "lifecycle" })
        {
            if (JsonFields.String(spec, field, "spec.", errors) is { } value)
            {
                attributes[field] = value;
            }
        }

        var cloneUrls = JsonFields.Strings(spec, "cloneUrls", "spec.", errors).ToList();
        if (JsonFields.String(spec, "cloneUrl", "spec.", errors) is { } cloneUrl)
        {
            cloneUrls.Insert(0, cloneUrl);
        }

        var aliases = JsonFields.Strings(spec, "aliases", "spec.", errors);
        if ((cloneUrls.Count > 0 || aliases.Count > 0) && kind != EntityKind.Repository)
        {
            errors.Add("spec.cloneUrl, spec.cloneUrls and spec.aliases apply only to kind Repository.");
        }

        foreach (var url in cloneUrls.Where(u => RepositoryUrl.Normalize(u) is null && !RepositoryUrl.IsLocalPath(u)))
        {
            errors.Add($"spec.cloneUrls: '{url}' is not a clone URL.");
        }

        if (cloneUrls.Count > 0)
        {
            attributes[RepositoryAttributes.CloneUrls] = RepositoryAttributes.JoinList(cloneUrls);
        }

        if (aliases.Count > 0)
        {
            attributes[RepositoryAttributes.Aliases] = RepositoryAttributes.JoinList(aliases);
        }

        if (!builder.AddEntity(
                self,
                JsonFields.String(metadata, "title", "metadata.", errors) ?? name ?? self.SimpleName,
                JsonFields.String(metadata, "description", "metadata.", errors),
                JsonFields.Strings(spec, "technologies", "spec.", errors),
                attributes))
        {
            errors.Add($"{self} is declared more than once.");
            return;
        }

        void One(string field, EntityKind defaultKind, Action<EntityRef> add)
        {
            if (JsonFields.String(spec, field, "spec.", errors) is { } value)
            {
                Resolve(field, value, defaultKind, add);
            }
        }

        void Many(string field, EntityKind defaultKind, Action<EntityRef> add)
        {
            foreach (var value in JsonFields.Strings(spec, field, "spec.", errors))
            {
                Resolve(field, value, defaultKind, add);
            }
        }

        void Resolve(string field, string value, EntityKind defaultKind, Action<EntityRef> add)
        {
            if (TryRef(value, defaultKind, out var other))
            {
                add(other);
            }
            else
            {
                errors.Add($"spec.{field}: '{value}' is not a valid entity reference.");
            }
        }

        One("system", EntityKind.System, system => builder.AddEdge(system, RelationType.Contains, self));
        One("domain", EntityKind.Domain, domain => builder.AddEdge(domain, RelationType.Contains, self));
        One("owner", EntityKind.Team, owner => builder.AddEdge(owner, RelationType.Owns, self));
        Many("repositories", EntityKind.Repository, repository =>
        {
            // Naming a repository here declares it; its binding to this component is explicit (R4).
            builder.AddReferenced(repository, []);
            builder.AddEdge(repository, RelationType.Implements, self);
        });
        Many("components", EntityKind.Component, component => builder.AddEdge(self, RelationType.Implements, component));
        Many("providesApis", EntityKind.Api, api => builder.AddEdge(self, RelationType.Provides, api));
        Many("consumesApis", EntityKind.Api, api => builder.AddEdge(self, RelationType.Consumes, api));
        Many("dependsOn", EntityKind.Component, dependency => builder.AddEdge(self, RelationType.DependsOn, dependency));
        Many("capabilities", EntityKind.Capability, capability => builder.AddEdge(self, RelationType.Supports, capability));
        Many("resources", EntityKind.Resource, resource => builder.AddEdge(
            self,
            resource.Kind is EntityKind.Database or EntityKind.DataProduct ? RelationType.StoresIn : RelationType.DependsOn,
            resource));
        Many("storesIn", EntityKind.Database, store => builder.AddEdge(self, RelationType.StoresIn, store));
        Many("publishesTo", EntityKind.EventStream, stream => builder.AddEdge(self, RelationType.PublishesTo, stream));
        Many("subscribesTo", EntityKind.EventStream, stream => builder.AddEdge(self, RelationType.SubscribesTo, stream));
        Many("deployments", EntityKind.Deployment, deployment => builder.AddEdge(self, RelationType.DeployedAs, deployment));
    }

    /// <summary>Accepts a full <c>kind:name</c> reference, or a bare name taken to be of the field's kind.</summary>
    private static bool TryRef(string value, EntityKind defaultKind, out EntityRef reference)
    {
        if (value.Contains(':', StringComparison.Ordinal))
        {
            return EntityRef.TryParse(value, out reference);
        }

        try
        {
            reference = new EntityRef(defaultKind, value);
            return true;
        }
        catch (FormatException)
        {
            reference = default;
            return false;
        }
    }

    private static ImmutableDictionary<string, EntityKind> BuildKinds()
    {
        var kinds = ImmutableDictionary.CreateBuilder<string, EntityKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            kinds[kind.ToString()] = kind;
            kinds[EntityRef.PrefixOf(kind)] = kind;
        }

        return kinds.ToImmutable();
    }
}
