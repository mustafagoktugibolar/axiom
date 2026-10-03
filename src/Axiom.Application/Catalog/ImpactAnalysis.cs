using System.Collections.Immutable;
using Axiom.Domain.Catalog;

namespace Axiom.Application.Catalog;

/// <summary>What is being changed. <see cref="Kind"/> is the caller's change classification (e.g. <c>api_or_contract</c>) and is echoed back.</summary>
public sealed record ImpactChange(string? Kind, EntityRef Entity);

public sealed record ImpactAnalysisRequest(string OrganizationId, ImpactChange Change, int Depth, bool IncludeUnconfirmed = false);

public enum ImpactConfidence
{
    /// <summary>Every hop on the path is a declared or human-confirmed fact.</summary>
    Confirmed,

    /// <summary>At least one hop is an inferred, unconfirmed relation.</summary>
    Candidate,
}

/// <summary>An entity affected by a change, the relationship path that reaches it, and who owns it.</summary>
public sealed record AffectedEntity(
    EntityRef Entity,
    int Depth,
    ImmutableArray<PathStep> Path,
    ImmutableArray<EntityRef> Owners,
    ImpactConfidence Confidence);

public sealed record ImpactAnalysis(
    ImpactChange Change,
    ImmutableArray<AffectedEntity> Affected,
    ImmutableArray<EntityRef> Owners,
    ImmutableArray<string> Gaps,
    bool Truncated,
    string CatalogVersion);

/// <summary>One relation-specific, single-direction traversal.</summary>
public sealed record ImpactLeg(TraversalDirection Direction, ImmutableArray<RelationType> Relations);

/// <summary>
/// How impact spreads from one kind of entity: a <see cref="Direct"/> traversal, plus <see cref="Pivots"/>,
/// single hops to neighbours which are then expanded with the plan of their own kind (a component's
/// provided APIs lead to those APIs' consumers).
/// </summary>
public sealed record ImpactPlan(ImpactLeg Direct, ImmutableArray<ImpactLeg> Pivots);

/// <summary>Chooses relations and direction per entity kind (system-graph.md, "Impact analysis").</summary>
public static class ImpactPlanner
{
    private static readonly ImpactPlan Api = Upstream(RelationType.Provides, RelationType.Consumes, RelationType.Implements, RelationType.Owns);

    private static readonly ImpactPlan Component = new(
        new ImpactLeg(TraversalDirection.Upstream, [RelationType.DependsOn, RelationType.Implements, RelationType.Owns]),
        [new ImpactLeg(TraversalDirection.Downstream, [RelationType.Provides])]);

    private static readonly ImpactPlan Repository = new(
        new ImpactLeg(TraversalDirection.Upstream, [RelationType.Owns]),
        [new ImpactLeg(TraversalDirection.Downstream, [RelationType.Implements])]);

    private static readonly ImpactPlan SharedResource = Upstream(
        RelationType.StoresIn, RelationType.PublishesTo, RelationType.SubscribesTo, RelationType.Consumes,
        RelationType.DependsOn, RelationType.Implements, RelationType.Owns);

    private static readonly ImpactPlan Container = new(new ImpactLeg(TraversalDirection.Downstream, [RelationType.Contains]), []);

    private static readonly ImpactPlan Team = new(new ImpactLeg(TraversalDirection.Downstream, [RelationType.Owns]), []);

    private static readonly ImpactPlan Capability = Upstream(RelationType.Supports, RelationType.Implements, RelationType.Owns);

    private static readonly ImpactPlan Deployment = Upstream(RelationType.DeployedAs, RelationType.Implements, RelationType.Owns);

    public static ImpactPlan For(EntityKind kind) => kind switch
    {
        // API ← PROVIDES provider, API ← CONSUMES consumers, then ← IMPLEMENTS repositories and ← OWNS teams.
        EntityKind.Api => Api,
        // Dependents (transitively), their repositories and owners; provided APIs lead on to their consumers.
        EntityKind.Component => Component,
        // The components the repository implements, each expanded as a component change.
        EntityKind.Repository => Repository,
        // Users, publishers and subscribers of a shared resource, then their repositories and owners.
        EntityKind.Database or EntityKind.Queue or EntityKind.EventStream or EntityKind.Resource or EntityKind.DataProduct => SharedResource,
        EntityKind.Organization or EntityKind.Domain or EntityKind.System => Container,
        EntityKind.Team => Team,
        EntityKind.Capability => Capability,
        EntityKind.Deployment or EntityKind.Environment => Deployment,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown entity kind."),
    };

    private static ImpactPlan Upstream(params RelationType[] relations) =>
        new(new ImpactLeg(TraversalDirection.Upstream, [.. relations]), []);
}

/// <summary>
/// Impact analysis use case (task 2.6): answers which entities, repositories and owners a change can
/// affect, using only bounded relation-specific traversals of the System Graph.
/// </summary>
public sealed class ImpactAnalyzer(ISystemGraph graph)
{
    /// <summary>Upper bound on pivot entities expanded per analysis; beyond it the result is marked truncated.</summary>
    public const int MaxPivotExpansions = 25;

    public async Task<ImpactAnalysis> AnalyzeAsync(ImpactAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OrganizationId);

        var state = new Accumulator(request, Math.Clamp(request.Depth, 1, ISystemGraph.MaxTraversalDepth));
        if (await graph.FindAsync(request.OrganizationId, request.Change.Entity, cancellationToken).ConfigureAwait(false) is null)
        {
            state.Gaps.Add($"{request.Change.Entity} is not described in the system graph; impact is derived only from relations that reference it.");
        }

        await ExpandAsync(state, request.Change.Entity, [], state.Depth, cancellationToken).ConfigureAwait(false);

        state.CatalogVersion ??= await graph.GetCatalogVersionAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        var affected = state.Reached.Values
            .OrderBy(a => a.Depth)
            .ThenBy(a => a.Entity)
            .Select(a => new AffectedEntity(
                a.Entity,
                a.Depth,
                a.Path,
                state.Owners.TryGetValue(a.Entity, out var owners) ? [.. owners] : [],
                a.IsFact ? ImpactConfidence.Confirmed : ImpactConfidence.Candidate))
            .ToImmutableArray();

        if (affected.Any(a => a.Confidence == ImpactConfidence.Candidate))
        {
            state.Gaps.Add("Some affected entities are reached only through inferred, unconfirmed relations and are candidates, not facts.");
        }

        return new ImpactAnalysis(
            request.Change,
            affected,
            [.. state.Owners.Values.SelectMany(o => o).Distinct().Order()],
            [.. state.Gaps],
            state.Truncated,
            state.CatalogVersion);
    }

    private async Task ExpandAsync(Accumulator state, EntityRef entity, ImmutableArray<PathStep> prefix, int remainingDepth, CancellationToken cancellationToken)
    {
        if (remainingDepth <= 0 || !state.Expanded.Add(entity))
        {
            return;
        }

        var plan = ImpactPlanner.For(entity.Kind);
        var direct = await TraverseAsync(state, entity, plan.Direct, remainingDepth, cancellationToken).ConfigureAwait(false);
        foreach (var impacted in direct.Impacted)
        {
            state.Record(impacted, prefix);
        }

        foreach (var pivot in plan.Pivots)
        {
            var hop = await TraverseAsync(state, entity, pivot, 1, cancellationToken).ConfigureAwait(false);
            foreach (var impacted in hop.Impacted)
            {
                state.Record(impacted, prefix);
                if (remainingDepth - 1 <= 0 || state.Expanded.Contains(impacted.Entity))
                {
                    continue;
                }

                if (state.PivotExpansions >= MaxPivotExpansions)
                {
                    state.Truncated = true;
                    continue;
                }

                state.PivotExpansions++;
                await ExpandAsync(state, impacted.Entity, prefix.AddRange(impacted.Path), remainingDepth - 1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ImpactResult> TraverseAsync(Accumulator state, EntityRef start, ImpactLeg leg, int depth, CancellationToken cancellationToken)
    {
        var result = await graph.TraverseAsync(
            state.Request.OrganizationId,
            new ImpactQuery(start, leg.Direction, leg.Relations, depth, state.Request.IncludeUnconfirmed),
            cancellationToken).ConfigureAwait(false);

        state.CatalogVersion ??= result.CatalogVersion;
        state.Truncated |= result.Truncated;
        foreach (var (owned, owners) in result.Owners)
        {
            if (!state.Owners.TryGetValue(owned, out var known))
            {
                state.Owners[owned] = known = [];
            }

            known.UnionWith(owners);
        }

        return result;
    }

    private sealed class Accumulator(ImpactAnalysisRequest request, int depth)
    {
        public ImpactAnalysisRequest Request { get; } = request;

        public int Depth { get; } = depth;

        public Dictionary<EntityRef, ImpactedEntity> Reached { get; } = [];

        public Dictionary<EntityRef, SortedSet<EntityRef>> Owners { get; } = [];

        public HashSet<EntityRef> Expanded { get; } = [];

        public List<string> Gaps { get; } = [];

        public int PivotExpansions { get; set; }

        public bool Truncated { get; set; }

        public string? CatalogVersion { get; set; }

        /// <summary>Keeps the shortest path per entity; among equally short paths a factual one wins, then the first found.</summary>
        public void Record(ImpactedEntity impacted, ImmutableArray<PathStep> prefix)
        {
            if (impacted.Entity == Request.Change.Entity)
            {
                return;
            }

            var candidate = prefix.IsEmpty
                ? impacted
                : new ImpactedEntity(impacted.Entity, prefix.Length + impacted.Depth, prefix.AddRange(impacted.Path));

            if (!Reached.TryGetValue(candidate.Entity, out var existing)
                || candidate.Depth < existing.Depth
                || (candidate.Depth == existing.Depth && candidate.IsFact && !existing.IsFact))
            {
                Reached[candidate.Entity] = candidate;
            }
        }
    }
}
