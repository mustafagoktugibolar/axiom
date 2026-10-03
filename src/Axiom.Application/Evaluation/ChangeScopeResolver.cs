using System.Collections.Immutable;
using System.Diagnostics;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;

namespace Axiom.Application.Evaluation;

/// <summary>What a change touches, as far as the request and the System Graph determine it.</summary>
public sealed record ChangeContext(
    EvaluationScope Scope,
    EntityRef? Repository,
    string RepositoryName,
    RepositoryTopology? Topology,
    ImmutableArray<string> Gaps,
    ImmutableArray<Finding> Findings,
    string CatalogVersion);

public sealed record ChangeScopeRequest(string OrganizationId, string Repository, ImmutableArray<string>? Paths, string? Environment);

/// <summary>
/// Steps 1–3 of the resolution pipeline (design.md §4.4): repository identity, direct topology and the
/// resulting evaluation scope. A dimension is marked known only when topology actually determines it,
/// so gaps keep records applicable rather than dropping them (R4).
/// </summary>
public sealed class ChangeScopeResolver(ISystemGraph graph)
{
    public const string RepositoryUnbound = "TOPOLOGY_REPOSITORY_UNBOUND";
    public const string TopologyGap = "TOPOLOGY_GAP";

    public async Task<ChangeContext> ResolveAsync(ChangeScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.ResolveScope);

        var catalogVersion = await graph.GetCatalogVersionAsync(request.OrganizationId, cancellationToken);
        var repository = await graph.ResolveRepositoryAsync(request.OrganizationId, request.Repository, cancellationToken);
        var scope = EvaluationScope.Empty.WithEntities(ScopeDimension.Organization, [new EntityRef(EntityKind.Organization, request.OrganizationId)]);

        if (request.Paths is { } paths)
        {
            scope = scope.With(ScopeDimension.Path, paths);
        }

        if (!string.IsNullOrWhiteSpace(request.Environment))
        {
            scope = scope.With(ScopeDimension.Environment, request.Environment);
        }

        if (repository is null)
        {
            var name = RepositoryDisplayName(request.Repository);
            var gap = $"Repository '{name}' is not bound to any component in the System Graph.";
            var finding = new Finding
            {
                Code = RepositoryUnbound,
                Severity = EnforcementLevel.Warn,
                Source = FindingSource.Precondition,
                Message = gap + " Records scoped by component, system or domain are treated as potentially applicable.",
                RecommendedAction = "Add a catalog manifest to the repository or bind it in the System Graph.",
                Evidence = [new EvidenceRef("catalog", Locator: name)],
            };
            activity?.SetTag("axiom.repository.bound", false);
            return new ChangeContext(scope.With(ScopeDimension.Repository, name), null, name, null, [gap], [finding], catalogVersion);
        }

        var topology = await graph.GetRepositoryTopologyAsync(request.OrganizationId, repository.Value, cancellationToken);
        scope = scope.WithEntities(ScopeDimension.Repository, [repository.Value]);

        // A dimension is known only when the graph actually declares it. An undeclared level, capability,
        // API, resource or technology is undetermined, not empty: the catalog being incomplete must
        // never remove governance.
        scope = WithDeclared(scope, ScopeDimension.Component, topology.Components);
        scope = WithDeclared(scope, ScopeDimension.System, topology.Systems);
        scope = WithDeclared(scope, ScopeDimension.Domain, topology.Domains);
        scope = WithDeclared(scope, ScopeDimension.Capability, topology.Capabilities);
        scope = WithDeclared(scope, ScopeDimension.Api, topology.Apis);
        scope = WithDeclared(scope, ScopeDimension.Resource, topology.Resources);
        if (!topology.Technologies.IsEmpty)
        {
            scope = scope.With(ScopeDimension.Technology, topology.Technologies);
        }

        var findings = topology.Gaps
            .Select(gap => new Finding
            {
                Code = TopologyGap,
                Severity = EnforcementLevel.Info,
                Source = FindingSource.Precondition,
                Message = gap,
                RecommendedAction = "Complete the catalog entry so governance can be resolved precisely.",
                Evidence = [new EvidenceRef("catalog", repository.Value.ToString())],
            })
            .ToImmutableArray();

        activity?.SetTag("axiom.repository.bound", true);
        return new ChangeContext(scope, repository, repository.Value.ToString(), topology, topology.Gaps, findings, catalogVersion);
    }

    private static EvaluationScope WithDeclared(EvaluationScope scope, ScopeDimension dimension, ImmutableArray<EntityRef> entities) =>
        entities.IsEmpty ? scope : scope.WithEntities(dimension, entities);

    private static string RepositoryDisplayName(string repository)
    {
        var trimmed = repository.Trim();
        return EntityRef.TryParse(trimmed, out var reference) ? reference.ToString() : trimmed;
    }
}
