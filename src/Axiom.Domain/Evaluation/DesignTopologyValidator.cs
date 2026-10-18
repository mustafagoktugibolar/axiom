using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Evaluation;

/// <summary>A repository the design declares, as bound in the System Graph, with the components it owns.</summary>
public sealed record DeclaredRepositoryFacts(string Name, EntityRef Repository, ImmutableArray<EntityRef> Components);

/// <summary>
/// An entity the design names with a typed reference (<c>kind:name</c>) in its text. <see cref="Connected"/> is
/// true when the graph has an edge (within the bounded depth) or a containment link tying it to the declared scope.
/// </summary>
public sealed record ReferencedEntity(EntityRef Entity, bool Known, bool Connected);

/// <summary>What the System Graph says about the neighbourhood of the design's declared scope (task 6.7).</summary>
public sealed record DesignTopologyFacts(
    ImmutableArray<DeclaredRepositoryFacts> Repositories,
    ImmutableArray<AffectedDependency> Dependents,
    ImmutableArray<ReferencedEntity> References,
    int DependencyDepth)
{
    public static DesignTopologyFacts Empty { get; } = new([], [], [], 0);
}

public static class DesignTopologyCodes
{
    public const string Mismatch = "DESIGN_TOPOLOGY_MISMATCH";
    public const string UnknownEntity = "DESIGN_UNKNOWN_ENTITY";
}

/// <summary>
/// Compares what a design declares with the topology: components of declared repositories the design never
/// mentions, dependents within the bounded depth it ignores, typed references the graph does not know, and
/// declared dependencies with no connecting edge. Pure and deterministic; the graph facts are gathered by the caller.
/// </summary>
public static partial class DesignTopologyValidator
{
    public const int MaxFindingsPerCheck = 10;

    [GeneratedRegex(@"\b(?:org|domain|system|component|repo|api|resource|database|queue|stream|dataproduct|deployment|capability|team|environment):[A-Za-z0-9][A-Za-z0-9._/\-]*[A-Za-z0-9]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TypedReference();

    /// <summary>Distinct, ordered typed entity references written in the design's prose.</summary>
    public static ImmutableArray<EntityRef> ExtractReferences(DesignDocument design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var text = string.Join("\n", design.Goal, design.CurrentBehavior, design.ProposedBehavior, design.DataFlow, design.Contracts, design.Migration, design.Rollout);
        return [.. TypedReference().Matches(text)
            .Select(m => EntityRef.TryParse(m.Value, out var reference) ? (EntityRef?)reference : null)
            .Where(r => r is not null)
            .Select(r => r!.Value)
            .Distinct()
            .Order()];
    }

    public static ImmutableArray<Finding> Validate(DesignDocument design, DesignTopologyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(facts);

        var findings = ImmutableArray.CreateBuilder<Finding>();
        var haystack = Normalize(string.Join("\n", design.Title, design.SubstantiveText, design.CurrentBehavior,
            string.Join(" ", design.AffectedSystems), string.Join(" ", design.AffectedRepositories)));
        bool Mentions(EntityRef entity) =>
            (entity.SimpleName.Length >= 3 && haystack.Contains(Normalize(entity.SimpleName), StringComparison.Ordinal))
            || haystack.Contains(Normalize(entity.Name), StringComparison.Ordinal);

        Finding Make(string code, EnforcementLevel severity, string message, string action, string locator, string entity) => new()
        {
            Code = code,
            Severity = severity,
            Source = FindingSource.Precondition,
            Message = message,
            RecommendedAction = action,
            Evidence = [new EvidenceRef("design", design.Id, Locator: locator), new EvidenceRef("catalog", entity)],
        };

        // 1. Components the declared repositories own that the design never mentions.
        foreach (var x in facts.Repositories
            .SelectMany(r => r.Components.Select(c => (Repository: r, Component: c)))
            .Where(x => !Mentions(x.Component))
            .DistinctBy(x => x.Component)
            .OrderBy(x => x.Component)
            .Take(MaxFindingsPerCheck))
        {
            findings.Add(Make(DesignTopologyCodes.Mismatch, EnforcementLevel.Warn,
                $"The design declares repository '{x.Repository.Name}', which owns {x.Component}, but never mentions that component.",
                $"Describe how {x.Component} is affected, or state that it is unchanged.",
                "section:affectedRepositories", x.Component.ToString()));
        }

        // 2. Dependents within the bounded depth that the design ignores.
        foreach (var dependent in facts.Dependents
            .Where(d => EntityRef.TryParse(d.Entity, out _) && !Mentions(EntityRef.Parse(d.Entity)))
            .OrderBy(d => d.Depth).ThenBy(d => d.Entity, StringComparer.Ordinal)
            .Take(MaxFindingsPerCheck))
        {
            var severity = dependent.IsFact && dependent.Depth <= 1 ? EnforcementLevel.RequireReview : EnforcementLevel.Warn;
            var owners = dependent.Owners.IsEmpty ? string.Empty : $" with {string.Join(", ", dependent.Owners)}";
            findings.Add(Make(DesignTopologyCodes.Mismatch, severity,
                $"{dependent.Entity} depends on the declared scope (depth {dependent.Depth}{(dependent.IsFact ? string.Empty : ", inferred")}) but the design does not address it. Path: {string.Join("; ", dependent.Path)}.",
                $"Assess the impact on {dependent.Entity}{owners} and record it under affected systems or data flow, or explain why it is unaffected.",
                "section:affectedSystems", dependent.Entity));
        }

        // 3. Typed references: unknown to the graph, or known but with no edge to the declared scope.
        foreach (var reference in facts.References.OrderBy(r => r.Entity))
        {
            if (!reference.Known)
            {
                findings.Add(Make(DesignTopologyCodes.UnknownEntity, EnforcementLevel.Warn,
                    $"The design refers to {reference.Entity}, which is not in the System Graph.",
                    "Correct the reference, or add the entity to the catalog so its impact can be assessed.",
                    "text", reference.Entity.ToString()));
            }
            else if (!reference.Connected)
            {
                findings.Add(Make(DesignTopologyCodes.Mismatch, EnforcementLevel.Warn,
                    $"The design declares {reference.Entity} as part of the change, but the System Graph has no relation within depth {facts.DependencyDepth} connecting it to the declared repositories.",
                    $"Add the missing relation to the catalog, or correct the design if {reference.Entity} is not actually involved.",
                    "text", reference.Entity.ToString()));
            }
        }

        return findings.ToImmutable();
    }

    private static string Normalize(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
