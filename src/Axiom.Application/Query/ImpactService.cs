using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;

namespace Axiom.Application.Query;

/// <summary>Input of <c>governance.impact_analysis</c> / <c>POST /v1/impact</c>.</summary>
public sealed record ImpactRequestDto(string? Repository, string? ChangeKind, string? EntityRef, int? Depth, bool IncludeUnconfirmed);

public sealed record AffectedEntityDto(string Entity, int Depth, ImmutableArray<string> Path, ImmutableArray<string> Owners, string Confidence);

public sealed record ImpactResultDto(
    string Change,
    string? ChangeKind,
    ImmutableArray<AffectedEntityDto> Affected,
    ImmutableArray<string> Owners,
    ImmutableArray<string> RelatedGovernance,
    ImmutableArray<string> Gaps,
    bool Truncated,
    string CatalogVersion);

/// <summary>
/// Who and what a change reaches (R14–R16): the affected entities with the relationship paths and
/// provenance, their owners, and the governance records scoped to them.
/// </summary>
public sealed class ImpactService(ImpactAnalyzer analyzer, ISystemGraph graph, IGovernanceQueries governance)
{
    public const int DefaultDepth = 2;
    private const int MaxGovernanceLookups = 25;

    private static readonly ImmutableDictionary<EntityKind, ScopeDimension> RelatedDimensions = new Dictionary<EntityKind, ScopeDimension>
    {
        [EntityKind.System] = ScopeDimension.System,
        [EntityKind.Component] = ScopeDimension.Component,
        [EntityKind.Repository] = ScopeDimension.Repository,
        [EntityKind.Api] = ScopeDimension.Api,
        [EntityKind.Domain] = ScopeDimension.Domain,
        [EntityKind.Capability] = ScopeDimension.Capability,
        [EntityKind.Resource] = ScopeDimension.Resource,
    }.ToImmutableDictionary();

    public async Task<ImpactResultDto> AnalyzeAsync(AxiomPrincipal principal, ImpactRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.ReadCatalog);

        var entity = await ResolveEntityAsync(principal.OrganizationId, request, cancellationToken);
        var depth = Math.Clamp(request.Depth ?? DefaultDepth, 1, ISystemGraph.MaxTraversalDepth);
        var analysis = await analyzer.AnalyzeAsync(
            new ImpactAnalysisRequest(principal.OrganizationId, new ImpactChange(request.ChangeKind, entity), depth, request.IncludeUnconfirmed), cancellationToken);

        var related = await RelatedGovernanceAsync(principal.OrganizationId, analysis, cancellationToken);
        return new ImpactResultDto(
            analysis.Change.Entity.ToString(),
            analysis.Change.Kind,
            [.. analysis.Affected.Select(a => new AffectedEntityDto(
                a.Entity.ToString(), a.Depth, [.. a.Path.Select(s => $"{s.From} -{s.Relation}-> {s.To}")], [.. a.Owners.Select(o => o.ToString())], a.Confidence.ToString()))],
            [.. analysis.Owners.Select(o => o.ToString())],
            related,
            analysis.Gaps,
            analysis.Truncated,
            analysis.CatalogVersion);
    }

    private async Task<EntityRef> ResolveEntityAsync(string organization, ImpactRequestDto request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.EntityRef))
        {
            return EntityRef.TryParse(request.EntityRef.Trim(), out var parsed)
                ? parsed
                : throw AxiomException.Invalid($"'{request.EntityRef}' is not a valid entity reference (for example 'api:orders/v2').");
        }

        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            throw AxiomException.Invalid("Provide 'entityRef' or 'repository'.");
        }

        return await graph.ResolveRepositoryAsync(organization, request.Repository.Trim(), cancellationToken)
            ?? throw AxiomException.NotFound($"Repository '{request.Repository}' in the System Graph");
    }

    private async Task<ImmutableArray<string>> RelatedGovernanceAsync(string organization, ImpactAnalysis analysis, CancellationToken cancellationToken)
    {
        var entities = analysis.Affected.Select(a => a.Entity).Prepend(analysis.Change.Entity).Distinct()
            .Where(e => RelatedDimensions.ContainsKey(e.Kind)).Take(MaxGovernanceLookups);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var result = await governance.SearchAsync(
                organization,
                new GovernanceQuery
                {
                    Scope = ImmutableDictionary<ScopeDimension, string>.Empty.Add(RelatedDimensions[entity.Kind], entity.Name),
                    Statuses = [LifecycleStatus.Accepted],
                    Take = 50,
                },
                cancellationToken);
            ids.UnionWith(result.Items.Where(i => i.IsAuthoritative).Select(i => i.Id));
        }

        return [.. ids];
    }
}
