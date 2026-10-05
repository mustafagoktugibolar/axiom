using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;

namespace Axiom.Api.Endpoints;

internal static class GraphEndpoints
{
    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder routes)
    {
        // The System Graph for the portal: nodes and edges, optionally the neighbourhood of one entity.
        routes.MapGet("/v1/graph", async (AxiomPrincipal principal, ISystemGraph graph, string? focus, int? depth, int? maxNodes, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ReadCatalog);
            EntityRef? start = null;
            if (!string.IsNullOrWhiteSpace(focus))
            {
                start = EntityRef.TryParse(focus, out var parsed) ? parsed : throw AxiomException.Invalid($"'{focus}' is not a valid entity reference (kind:name).");
            }

            var view = await graph.GetViewAsync(principal.OrganizationId, start, Math.Clamp(depth ?? 2, 1, ISystemGraph.MaxTraversalDepth), Math.Clamp(maxNodes ?? 100, 1, 300), ct);
            return new
            {
                truncated = view.Truncated,
                nodes = view.Nodes.Select(n => new { id = n.Ref.ToString(), kind = n.Ref.Kind.ToString().ToLowerInvariant(), title = n.Title, technologies = n.Technologies }),
                edges = view.Edges.Select(e => new { from = e.From.ToString(), relation = e.Relation.ToString(), to = e.To.ToString(), fact = e.Provenance.IsFact }),
            };
        }).WithTags("Catalog").WithName("getGraph");
        return routes;
    }
}
