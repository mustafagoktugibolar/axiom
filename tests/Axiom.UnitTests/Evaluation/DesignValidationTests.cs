using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Governance.Parsing;
using Axiom.UnitTests.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Evaluation;

public class DesignValidationTests
{
    private const string Org = "acme";
    private readonly FakeSnapshots _snapshots = new();
    private readonly FakeGraph _graph = new();
    private readonly InMemoryEvaluationStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly DesignValidationService _service;
    private readonly AxiomPrincipal _developer = Principals.With(Org, Role.Contributor);

    public DesignValidationTests()
    {
        _service = new DesignValidationService(new EvaluationPipeline(_snapshots, new ChangeScopeResolver(_graph), _graph, _store, _time), _graph);
        foreach (var repository in new[] { "gateway", "homepage-service" })
        {
            _graph.Bind(new RepositoryTopology(EntityRef.Parse($"repo:{repository}"), [EntityRef.Parse("component:gui/api-gateway")],
                [EntityRef.Parse("system:gui-platform")], [], [], [], [], [EntityRef.Parse("team:platform")], [], []));
        }

        _graph.Known.Add(EntityRef.Parse("system:gui-platform"));
        _snapshots.Publish(Org, new GovernanceRecordParser().Parse("governance/decisions/ARCH-042.md", Example("decisions/ARCH-042-gateway-routing.md")).Record!);
    }

    private static string Example(string relative) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SpecExamples", relative));

    private static string SpecDesign => Example("designs/DESIGN-101-homepage-routing.md");

    private Task<StoredEvaluation> ValidateAsync(string markdown) =>
        _service.ExecuteAsync(_developer, new DesignValidationRequest(Org, "gateway", "feature/homepage", null, markdown, "ev_pre", "kiro", null), default);

    [Fact]
    public void Parses_the_specification_design_example()
    {
        var design = DesignParser.ParseMarkdown(SpecDesign).Design!;

        Assert.Equal("DESIGN-101", design.Id);
        Assert.Equal("Homepage Routing Change", design.Title);
        Assert.Equal(["gui-platform"], design.AffectedSystems.AsEnumerable());
        Assert.Equal(["gateway", "homepage-service"], design.AffectedRepositories.AsEnumerable());
        Assert.Equal(["ARCH-042"], design.DecisionsConsulted.AsEnumerable());
        Assert.Equal(2, design.NonGoals.Length);
        Assert.Contains("sequenceDiagram", design.DataFlow, StringComparison.Ordinal);
        Assert.NotEmpty(design.Rollout);
    }

    [Fact]
    public async Task Specification_design_passes_the_gate_and_is_bound_to_its_hash()
    {
        var (evaluation, receipt) = await ValidateAsync(SpecDesign);

        Assert.Equal(Verdict.Allow, evaluation.Verdict);
        Assert.Equal(EvaluationStage.Design, evaluation.Stage);
        Assert.Equal("DESIGN-101", evaluation.DesignId);
        Assert.Equal(DesignParser.ParseMarkdown(SpecDesign).Design!.MaterialHash, evaluation.DesignHash);
        Assert.Equal("ev_pre", evaluation.ParentEvaluationId);
        Assert.Contains(evaluation.DesignHash!, receipt.Payload, StringComparison.Ordinal);
        Assert.Equal([DesignValidationService.Implement], evaluation.RequiredActions.AsEnumerable());
    }

    [Fact]
    public void Formatting_changes_keep_the_material_hash_and_substantive_changes_do_not()
    {
        var original = DesignParser.ParseMarkdown(SpecDesign).Design!;
        var reformatted = DesignParser.ParseMarkdown(SpecDesign.Replace("Move role-specific", "Move   role-specific\n", StringComparison.Ordinal)).Design!;
        var changed = DesignParser.ParseMarkdown(SpecDesign.Replace("into Homepage Service", "into the API Gateway", StringComparison.Ordinal)).Design!;

        Assert.Equal(original.MaterialHash, reformatted.MaterialHash);
        Assert.NotEqual(original.MaterialHash, changed.MaterialHash);
    }

    [Theory]
    [InlineData("## Goal", "goal")]
    [InlineData("## Failure modes", "failureModes")]
    [InlineData("## Observability", "observability")]
    [InlineData("## Rollout", "rollout")]
    [InlineData("## Non-goals", "nonGoals")]
    public async Task Missing_mandatory_section_blocks_with_an_actionable_finding(string heading, string section)
    {
        var (evaluation, _) = await ValidateAsync(SpecDesign.Replace(heading, "## Notes", StringComparison.Ordinal));

        Assert.Equal(Verdict.Block, evaluation.Verdict);
        var finding = Assert.Single(evaluation.Findings, f => f.Code == DesignFindingCodes.SectionMissing);
        Assert.Contains($"'{section}'", finding.Message, StringComparison.Ordinal);
        Assert.Contains(finding.RecommendedAction!, evaluation.RequiredActions);
    }

    [Fact]
    public async Task Applicable_decision_must_be_acknowledged_by_exact_id()
    {
        var (evaluation, _) = await ValidateAsync(SpecDesign.Replace("- ARCH-042", "- none", StringComparison.Ordinal));

        var finding = Assert.Single(evaluation.Findings, f => f.Code == DesignFindingCodes.DecisionNotAcknowledged);
        Assert.Equal(["ARCH-042"], finding.GovernanceIds.AsEnumerable());
        Assert.Equal("1", Assert.Single(finding.Evidence).Revision);
        Assert.Equal(Verdict.Block, evaluation.Verdict);
    }

    [Fact]
    public async Task Design_proposing_a_forbidden_approach_gets_the_decisions_verdict_and_its_id()
    {
        var (evaluation, _) = await ValidateAsync(SpecDesign.Replace(
            "Gateway forwards authenticated identity/context to Homepage Service.",
            "We add domain database access to the Gateway for routing decisions.", StringComparison.Ordinal));

        var finding = Assert.Single(evaluation.Findings, f => f.Code == DesignFindingCodes.ContradictsDecision);
        Assert.Equal(["ARCH-042"], finding.GovernanceIds.AsEnumerable());
        Assert.Equal(EnforcementLevel.RequireReview, finding.Severity);
        Assert.Contains("Homepage Service", finding.RecommendedAction!, StringComparison.Ordinal);
        Assert.Equal(Verdict.RequireReview, evaluation.Verdict);
        Assert.Contains("platform-architecture", evaluation.RequiredReviewers);
    }

    [Fact]
    public async Task Citing_a_superseded_decision_points_to_its_successor()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("ARCH-001", LifecycleStatus.Superseded),
            TestRecords.Decision("ARCH-002", relations: [new Relation(RelationKind.Supersedes, "ARCH-001")]));

        var (evaluation, _) = await ValidateAsync(SpecDesign.Replace("- ARCH-042", "- ARCH-001\n- ARCH-002\n- ARCH-999", StringComparison.Ordinal));

        var stale = Assert.Single(evaluation.Findings, f => f.Code == DesignFindingCodes.ConsultedNotAuthoritative);
        Assert.Equal(["ARCH-001", "ARCH-002"], stale.GovernanceIds.AsEnumerable());
        Assert.Contains(evaluation.Findings, f => f.Code == DesignFindingCodes.ConsultedUnknown && f.GovernanceIds.Contains("ARCH-999"));
    }

    [Fact]
    public async Task Architectural_choice_without_a_decision_candidate_requires_review()
    {
        var design = SpecDesign.Replace("Move role-specific homepage resolution", "Add a new service and move role-specific homepage resolution", StringComparison.Ordinal);

        var (without, _) = await ValidateAsync(design);
        var (with, _) = await ValidateAsync(design + "\n\n## New decision candidates\n- Homepage Service owns homepage resolution\n");

        Assert.Contains(without.Findings, f => f.Code == DesignFindingCodes.UnrecordedDecision && f.Severity == EnforcementLevel.RequireReview);
        Assert.DoesNotContain(with.Findings, f => f.Code == DesignFindingCodes.UnrecordedDecision);
    }

    [Fact]
    public async Task Declared_scope_is_checked_against_the_system_graph()
    {
        var (evaluation, _) = await ValidateAsync(SpecDesign
            .Replace("- gui-platform", "- billing", StringComparison.Ordinal)
            .Replace("homepage-service / homepage-service", "homepage-service / ghost-repo", StringComparison.Ordinal));

        var messages = evaluation.Findings.Where(f => f.Code == DesignFindingCodes.ScopeMismatch).Select(f => f.Message).ToArray();
        Assert.Contains(messages, m => m.Contains("'gui-platform', which the design does not list", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("system 'billing'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("repository 'ghost-repo'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Structured_design_is_validated_like_markdown()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            id = "DESIGN-200", title = "Tune cache", goal = "Reduce latency", nonGoals = Array.Empty<string>(), affectedSystems = new[] { "gui-platform" },
            currentBehavior = "Slow", proposedBehavior = "Faster", decisionsConsulted = new[] { "ARCH-042" },
            failureModes = new[] { new { mode = "cache down" } }, observability = new { metrics = "latency" }, rollout = new { plan = "flag" },
        });

        var (evaluation, _) = await _service.ExecuteAsync(_developer, new DesignValidationRequest(Org, "gateway", "main", json, null, null, null, null), default);

        Assert.Equal(Verdict.Allow, evaluation.Verdict);
    }

    [Fact]
    public async Task Request_must_carry_exactly_one_design_form()
    {
        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            _service.ExecuteAsync(_developer, new DesignValidationRequest(Org, "gateway", "main", null, null, null, null, null), default));
        Assert.Equal(ErrorKind.Validation, error.Kind);
    }
}
