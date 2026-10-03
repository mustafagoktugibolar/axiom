using System.Collections.Immutable;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;

namespace Axiom.Domain.Evaluation;

public static class DesignFindingCodes
{
    public const string SectionMissing = "DESIGN_SECTION_MISSING";
    public const string DecisionNotAcknowledged = "DESIGN_DECISION_NOT_ACKNOWLEDGED";
    public const string ConsultedUnknown = "DESIGN_CONSULTED_UNKNOWN_RECORD";
    public const string ConsultedNotAuthoritative = "DESIGN_CONSULTED_RECORD_NOT_AUTHORITATIVE";
    public const string ContradictsDecision = "DESIGN_CONTRADICTS_DECISION";
    public const string UnrecordedDecision = "DESIGN_UNRECORDED_ARCHITECTURAL_DECISION";
    public const string ScopeMismatch = "DESIGN_SCOPE_MISMATCH";
    public const string UnownedDependency = "DESIGN_UNOWNED_DEPENDENCY";
    public const string Unparseable = "DESIGN_UNPARSEABLE";
}

/// <summary>What the System Graph knows that the design's declared scope is checked against.</summary>
public sealed record DesignTopology(
    ImmutableArray<string> RepositorySystems,
    ImmutableArray<string> UnknownSystems,
    ImmutableArray<string> UnknownRepositories,
    ImmutableArray<string> UnownedEntities);

/// <summary>
/// Deterministic design gate (R6, design.md §8.2). Every finding names the exact section or governance
/// ID and the action that resolves it. Semantic contradiction analysis is a separate, advisory step.
/// </summary>
public static class DesignValidator
{
    /// <summary>Change classes that amount to making an architectural decision.</summary>
    private static readonly ImmutableHashSet<string> DecisionClasses =
    [
        ChangeClasses.NewComponent, ChangeClasses.StrategicTechnology, ChangeClasses.CrossSystemDependency,
        ChangeClasses.SecurityBoundary, ChangeClasses.DataModel, ChangeClasses.ArchitecturalDecision,
    ];

    public static ImmutableArray<Finding> Validate(
        DesignDocument design,
        ResolutionResult resolution,
        GovernanceSnapshot snapshot,
        SignificanceAssessment significance,
        DesignTopology topology)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(significance);
        ArgumentNullException.ThrowIfNull(topology);

        var findings = ImmutableArray.CreateBuilder<Finding>();
        var classes = significance.ChangeClasses.ToHashSet(StringComparer.Ordinal);

        ValidateSections(design, classes, findings);
        ValidateConsulted(design, resolution, snapshot, findings);
        ValidateContradictions(design, resolution, findings);
        ValidateUnrecordedDecisions(design, significance, findings);
        ValidateScope(design, topology, findings);
        return findings.ToImmutable();
    }

    private static void ValidateSections(DesignDocument design, HashSet<string> classes, ImmutableArray<Finding>.Builder findings)
    {
        void Require(bool present, string section, string why)
        {
            if (!present)
            {
                findings.Add(new Finding
                {
                    Code = DesignFindingCodes.SectionMissing,
                    Severity = EnforcementLevel.Block,
                    Source = FindingSource.Precondition,
                    Message = $"The design is missing the mandatory '{section}' section. {why}",
                    RecommendedAction = $"Add a '{section}' section to the design and validate it again.",
                    Evidence = [new EvidenceRef("design", design.Id, Locator: $"section:{section}")],
                });
            }
        }

        static bool Has(string text) => !string.IsNullOrWhiteSpace(text);
        const string always = "It is required for every significant change.";

        Require(Has(design.Id), "id", always);
        Require(Has(design.Goal), "goal", always);
        Require(design.NonGoalsStated, "nonGoals", always);
        Require(!design.AffectedSystems.IsEmpty, "affectedSystems", always);
        Require(Has(design.CurrentBehavior), "currentBehavior", always);
        Require(Has(design.ProposedBehavior), "proposedBehavior", always);
        Require(design.DecisionsConsultedStated, "decisionsConsulted", always);
        Require(Has(design.FailureModes), "failureModes", always);
        Require(Has(design.Observability), "observability", always);
        Require(Has(design.Rollout), "rollout", always);

        // R6: these are mandatory "where relevant"; relevance comes from the change classes.
        if (classes.Contains(ChangeClasses.SecurityBoundary))
        {
            Require(Has(design.Security), "security", "The change touches a security boundary.");
        }

        if (classes.Overlaps([ChangeClasses.DataModel, ChangeClasses.PublicContract, ChangeClasses.Migration]))
        {
            Require(Has(design.Migration), "migration", "The change alters data or a public contract, so compatibility and migration must be stated.");
        }

        if (classes.Contains(ChangeClasses.PublicContract))
        {
            Require(Has(design.Contracts), "contracts", "The change alters a public contract.");
        }

        if (classes.Overlaps([ChangeClasses.CrossSystemDependency, ChangeClasses.MultiSystem, ChangeClasses.NewComponent]))
        {
            Require(Has(design.DataFlow), "dataFlow", "The change spans components or systems, so data and control flow must be stated.");
        }
    }

    private static void ValidateConsulted(DesignDocument design, ResolutionResult resolution, GovernanceSnapshot snapshot, ImmutableArray<Finding>.Builder findings)
    {
        var consulted = design.DecisionsConsulted.ToHashSet(StringComparer.Ordinal);

        foreach (var applied in resolution.Applicable.Where(a => a.Importance == Importance.Required && !consulted.Contains(a.Revision.Id)))
        {
            findings.Add(new Finding
            {
                Code = DesignFindingCodes.DecisionNotAcknowledged,
                Severity = EnforcementLevel.Block,
                Source = FindingSource.Precondition,
                Message = $"{applied.Revision.Id} ({applied.Record.Title}) applies to this change but is not listed under decisions consulted.",
                GovernanceIds = [applied.Revision.Id],
                RecommendedAction = $"Read {applied.Revision.Id}, make the design consistent with it, and list it under decisions consulted.",
                Evidence = [RecordEvidence(applied.Revision)],
            });
        }

        foreach (var id in design.DecisionsConsulted.Order(StringComparer.Ordinal))
        {
            var revision = snapshot.Find(id);
            if (revision is null)
            {
                findings.Add(new Finding
                {
                    Code = DesignFindingCodes.ConsultedUnknown,
                    Severity = EnforcementLevel.Warn,
                    Source = FindingSource.Precondition,
                    Message = $"The design cites {id}, which does not exist in the governance snapshot.",
                    GovernanceIds = [id],
                    RecommendedAction = "Correct the reference, or propose the decision if it is missing.",
                    Evidence = [new EvidenceRef("design", design.Id, Locator: "section:decisionsConsulted")],
                });
            }
            else if (revision.Record.Status is not LifecycleStatus.Accepted && revision.Record.Kind != RecordKind.Exception)
            {
                var successors = revision.Record.RelatedIds(RelationKind.SupersededBy)
                    .Concat(snapshot.Revisions.Where(r => r.Record.RelatedIds(RelationKind.Supersedes).Contains(id, StringComparer.Ordinal)).Select(r => r.Id))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                findings.Add(new Finding
                {
                    Code = DesignFindingCodes.ConsultedNotAuthoritative,
                    Severity = EnforcementLevel.RequireReview,
                    Source = FindingSource.Precondition,
                    Message = $"The design relies on {id}, which is '{revision.Record.Status.ToString().ToLowerInvariant()}' and does not govern."
                        + (successors.Length > 0 ? $" It was replaced by {string.Join(", ", successors)}." : string.Empty),
                    GovernanceIds = [id, .. successors],
                    RecommendedAction = successors.Length > 0 ? $"Base the design on {string.Join(", ", successors)} instead." : "Do not treat this record as authority.",
                    Evidence = [RecordEvidence(revision)],
                });
            }
        }
    }

    private static void ValidateContradictions(DesignDocument design, ResolutionResult resolution, ImmutableArray<Finding>.Builder findings)
    {
        var text = Words(design.SubstantiveText);
        foreach (var applied in resolution.Applicable.Where(a => a.RefinedBy.IsEmpty && a.OverriddenBy.IsEmpty))
        {
            foreach (var forbidden in applied.Record.Forbidden)
            {
                var phrase = Words(forbidden);
                if (phrase.Length < 3 || !Contains(text, phrase))
                {
                    continue;
                }

                var waiver = applied.WaivedBy.FirstOrDefault();
                findings.Add(new Finding
                {
                    Code = DesignFindingCodes.ContradictsDecision,
                    Severity = applied.Record.IsNonExemptable ? EnforcementLevel.Block : applied.Record.Enforcement.DefaultVerdict,
                    Source = FindingSource.Precondition,
                    Message = $"The design proposes what {applied.Revision.Id} forbids: \"{forbidden}\".",
                    GovernanceIds = [applied.Revision.Id],
                    RecommendedAction = applied.Record.Preferred.IsEmpty
                        ? $"Change the design to comply with {applied.Revision.Id}, or request a scoped exception."
                        : $"Preferred by {applied.Revision.Id}: {string.Join(" ", applied.Record.Preferred)} Otherwise request a scoped exception.",
                    Evidence = [RecordEvidence(applied.Revision), new EvidenceRef("design", design.Id, Locator: "text")],
                    WaivedBy = applied.Record.IsNonExemptable ? null : waiver,
                });
            }
        }
    }

    private static void ValidateUnrecordedDecisions(DesignDocument design, SignificanceAssessment significance, ImmutableArray<Finding>.Builder findings)
    {
        var decisive = significance.Triggers.Where(t => DecisionClasses.Contains(t.ChangeClass)).ToArray();
        if (decisive.Length == 0 || !design.NewDecisionCandidates.IsEmpty)
        {
            return;
        }

        findings.Add(new Finding
        {
            Code = DesignFindingCodes.UnrecordedDecision,
            Severity = EnforcementLevel.RequireReview,
            Source = FindingSource.Precondition,
            Message = "The design makes an architectural choice but records no decision candidate: "
                + string.Join("; ", decisive.Select(t => $"{t.Code} ({string.Join(", ", t.Evidence)})")),
            RecommendedAction = "Propose the decision with governance.propose_decision and list it under new decision candidates, or cite the accepted decision that already covers this choice.",
            Evidence = [new EvidenceRef("design", design.Id, Locator: "section:newDecisionCandidates")],
        });
    }

    private static void ValidateScope(DesignDocument design, DesignTopology topology, ImmutableArray<Finding>.Builder findings)
    {
        void Add(string code, EnforcementLevel severity, string message, string action) => findings.Add(new Finding
        {
            Code = code,
            Severity = severity,
            Source = FindingSource.Precondition,
            Message = message,
            RecommendedAction = action,
            Evidence = [new EvidenceRef("design", design.Id, Locator: "section:affectedSystems"), new EvidenceRef("catalog")],
        });

        var declared = design.AffectedSystems.Select(Simple).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var system in topology.RepositorySystems.Where(s => !declared.Contains(Simple(s))).Order(StringComparer.Ordinal))
        {
            Add(DesignFindingCodes.ScopeMismatch, EnforcementLevel.RequireReview,
                $"The repository belongs to system '{system}', which the design does not list as affected.",
                $"Add '{system}' to affected systems or explain why it is not affected.");
        }

        foreach (var system in topology.UnknownSystems.Order(StringComparer.Ordinal))
        {
            Add(DesignFindingCodes.ScopeMismatch, EnforcementLevel.Warn,
                $"The design lists system '{system}', which is not in the System Graph.",
                "Correct the name or add the system to the catalog.");
        }

        foreach (var repository in topology.UnknownRepositories.Order(StringComparer.Ordinal))
        {
            Add(DesignFindingCodes.ScopeMismatch, EnforcementLevel.Warn,
                $"The design lists repository '{repository}', which is not in the System Graph.",
                "Correct the name or bind the repository in the catalog.");
        }

        foreach (var entity in topology.UnownedEntities.Order(StringComparer.Ordinal))
        {
            Add(DesignFindingCodes.UnownedDependency, EnforcementLevel.RequireReview,
                $"'{entity}' is affected by the design but has no owner in the System Graph, so nobody can review the impact.",
                "Assign an owner in the catalog before the design is approved.");
        }
    }

    private static string Simple(string name) => name[(name.LastIndexOfAny([':', '/']) + 1)..].Trim();

    private static EvidenceRef RecordEvidence(RecordRevision revision) => new(
        "decision", revision.Id, revision.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), revision.Provenance.Path, revision.Record.ContentHash);

    private static string[] Words(string text) =>
        new string([.. text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')]).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool Contains(string[] haystack, string[] needle)
    {
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
