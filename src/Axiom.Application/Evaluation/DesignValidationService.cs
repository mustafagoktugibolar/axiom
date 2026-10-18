using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;

namespace Axiom.Application.Evaluation;

/// <summary>
/// Input of <c>governance.validate_design</c> / <c>POST /v1/evaluations/design</c>. Exactly one of
/// <see cref="Design"/> (structured, per design.schema.json) or <see cref="DesignMarkdown"/> is given.
/// </summary>
public sealed record DesignValidationRequest(
    string? Organization,
    string Repository,
    string Ref,
    JsonElement? Design,
    string? DesignMarkdown,
    string? PreflightEvaluationId,
    string? HarnessType,
    string? HarnessSessionId);

/// <summary>
/// Design gate (R6): validates a significant-change design against applicable governance before
/// implementation is authorized. The approved scope is the scope recorded on the resulting evaluation,
/// and it is bound to the design's material hash.
/// </summary>
public sealed class DesignValidationService(EvaluationPipeline pipeline, ISystemGraph graph)
{
    public const string Implement = "Implement within the validated scope, then call governance.validate_diff with this design evaluation ID.";

    public async Task<StoredEvaluation> ExecuteAsync(AxiomPrincipal principal, DesignValidationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.Evaluate, request.Organization);
        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.DesignValidate);

        var design = Parse(request);
        var session = await pipeline.BeginAsync(
            new EvaluationRequest
            {
                Principal = principal,
                Stage = EvaluationStage.Design,
                Repository = RequestLimits.Identifier(request.Repository, "repository"),
                Ref = RequestLimits.Identifier(request.Ref, "ref"),
                Task = string.Join("\n", design.Title, design.Goal, design.ProposedBehavior),
                HarnessType = RequestLimits.OptionalIdentifier(request.HarnessType, "harness.type"),
                HarnessSessionId = RequestLimits.OptionalIdentifier(request.HarnessSessionId, "harness.sessionId"),
                ParentEvaluationId = RequestLimits.OptionalIdentifier(request.PreflightEvaluationId, "preflightEvaluationId"),
                DesignId = design.Id.Length > 0 ? design.Id : null,
                DesignHash = design.MaterialHash,
                DeclaredSystemCount = design.AffectedSystems.Length,
                DeclaredRepositoryCount = design.AffectedRepositories.Length,
            },
            cancellationToken);

        if (session.Existing is null)
        {
            var topology = await DescribeTopologyAsync(principal.OrganizationId, design, session, cancellationToken);
            session.Findings.AddRange(DesignValidator.Validate(design, session.Resolution, session.Snapshot, session.Significance, topology));

            foreach (var finding in session.Findings.Where(f => !f.IsWaived && f.Severity >= EnforcementLevel.RequireReview && f.RecommendedAction is not null))
            {
                session.RequiredActions.Add(finding.RecommendedAction!);
            }

            if (session.RequiredActions.Count == 0)
            {
                session.RequiredActions.Add(Implement);
            }
        }

        var stored = await pipeline.CompleteAsync(session, cancellationToken);
        activity?.SetTag("axiom.evaluation.id", stored.Evaluation.Id);
        activity?.SetTag("axiom.verdict", VerdictLattice.ToContract(stored.Evaluation.Verdict));
        return stored;
    }

    private static DesignDocument Parse(DesignValidationRequest request)
    {
        DesignParseResult result;
        if (request.Design is { } json && string.IsNullOrWhiteSpace(request.DesignMarkdown))
        {
            if (json.GetRawText().Length > RequestLimits.MaxDesignLength)
            {
                throw AxiomException.Invalid($"The design exceeds {RequestLimits.MaxDesignLength} characters.");
            }

            result = DesignParser.ParseJson(json);
        }
        else if (request.Design is null && RequestLimits.Text(request.DesignMarkdown, "designMarkdown", RequestLimits.MaxDesignLength) is { } markdown)
        {
            result = DesignParser.ParseMarkdown(markdown);
        }
        else
        {
            throw AxiomException.Invalid("Provide exactly one of 'design' (structured) or 'designMarkdown'.");
        }

        return result.Design ?? throw AxiomException.Invalid("The design could not be parsed: " + string.Join(" ", result.Errors));
    }

    private async Task<DesignTopology> DescribeTopologyAsync(string organization, DesignDocument design, EvaluationSession session, CancellationToken cancellationToken)
    {
        var unknownSystems = ImmutableArray.CreateBuilder<string>();
        foreach (var system in design.AffectedSystems)
        {
            if (!EntityRef.TryParse(system.Contains(':', StringComparison.Ordinal) ? system : $"system:{system}", out var reference)
                || reference.Kind != EntityKind.System
                || await graph.FindAsync(organization, reference, cancellationToken) is null)
            {
                unknownSystems.Add(system);
            }
        }

        var unknownRepositories = ImmutableArray.CreateBuilder<string>();
        foreach (var repository in design.AffectedRepositories)
        {
            if (await graph.ResolveRepositoryAsync(organization, repository, cancellationToken) is null)
            {
                unknownRepositories.Add(repository);
            }
        }

        var repositorySystems = session.Context.Topology?.Systems.Select(s => s.Name).ToImmutableArray() ?? [];
        var unowned = session.AffectedDependencies.Where(d => d.IsFact && d.Owners.IsEmpty).Select(d => d.Entity).ToImmutableArray();
        var facts = await GatherFactsAsync(organization, design, session, cancellationToken);
        return new DesignTopology(repositorySystems, unknownSystems.ToImmutable(), unknownRepositories.ToImmutable(), unowned) { Facts = facts };
    }

    /// <summary>Upper bound on components expanded for the depth checks, keeping the design gate's graph cost bounded.</summary>
    public const int MaxExpandedComponents = 25;

    private static readonly ImmutableArray<RelationType> OutgoingRelations =
        [RelationType.DependsOn, RelationType.Consumes, RelationType.StoresIn, RelationType.PublishesTo, RelationType.SubscribesTo];

    private async Task<DesignTopologyFacts> GatherFactsAsync(string organization, DesignDocument design, EvaluationSession session, CancellationToken cancellationToken)
    {
        var depth = EvaluationPipeline.DependencyDepth;
        var repositories = ImmutableArray.CreateBuilder<DeclaredRepositoryFacts>();
        var scope = new HashSet<EntityRef>();
        var components = new SortedSet<EntityRef>();

        if (session.Context.Topology is { } requested)
        {
            scope.UnionWith(requested.Components.Concat(requested.Apis).Concat(requested.Resources).Concat(requested.Systems).Append(requested.Repository));
            components.UnionWith(requested.Components);
        }

        foreach (var name in design.AffectedRepositories.Order(StringComparer.Ordinal))
        {
            if (await graph.ResolveRepositoryAsync(organization, name, cancellationToken) is not { } repository)
            {
                continue;
            }

            var topology = await graph.GetRepositoryTopologyAsync(organization, repository, cancellationToken);
            repositories.Add(new DeclaredRepositoryFacts(name, repository, topology.Components));
            scope.UnionWith(topology.Components.Concat(topology.Apis).Concat(topology.Resources).Concat(topology.Systems).Append(repository));
            components.UnionWith(topology.Components);
        }

        var dependents = new SortedDictionary<EntityRef, AffectedDependency>();
        var connected = new HashSet<EntityRef>(scope);
        foreach (var component in components.Take(MaxExpandedComponents))
        {
            var upstream = await graph.TraverseAsync(organization, new ImpactQuery(component, TraversalDirection.Upstream, [RelationType.DependsOn], depth, true), cancellationToken);
            foreach (var impacted in upstream.Impacted.Where(i => !scope.Contains(i.Entity)))
            {
                connected.Add(impacted.Entity);
                if (dependents.TryGetValue(impacted.Entity, out var known) && known.Depth <= impacted.Depth)
                {
                    continue;
                }

                var owners = upstream.Owners.TryGetValue(impacted.Entity, out var o) ? o.Select(x => x.ToString()).ToImmutableArray() : [];
                dependents[impacted.Entity] = new AffectedDependency(
                    impacted.Entity.ToString(), impacted.Depth, [.. impacted.Path.Select(s => $"{s.From} -{s.Relation}-> {s.To}")], owners, impacted.IsFact);
            }

            var downstream = await graph.TraverseAsync(organization, new ImpactQuery(component, TraversalDirection.Downstream, OutgoingRelations, depth, true), cancellationToken);
            connected.UnionWith(downstream.Impacted.Select(i => i.Entity));
        }

        var references = ImmutableArray.CreateBuilder<ReferencedEntity>();
        foreach (var reference in DesignTopologyValidator.ExtractReferences(design))
        {
            var known = reference.Kind == EntityKind.Repository
                ? await graph.ResolveRepositoryAsync(organization, reference.Name, cancellationToken) is not null
                : await graph.FindAsync(organization, reference, cancellationToken) is not null;
            references.Add(new ReferencedEntity(reference, known, connected.Contains(reference)));
        }

        return new DesignTopologyFacts(repositories.ToImmutable(), [.. dependents.Values], references.ToImmutable(), depth);
    }
}
