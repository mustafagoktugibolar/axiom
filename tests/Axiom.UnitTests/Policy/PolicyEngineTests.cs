using System.Collections.Immutable;
using Axiom.Application.Policy;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;
using Axiom.UnitTests.Governance;
using static Axiom.UnitTests.Policy.PolicyTestKit;

namespace Axiom.UnitTests.Policy;

public class PolicyEngineTests
{
    private static readonly Scope Gateway = Scope.Of((ScopeDimension.Repository, ["gateway"]));
    private readonly PolicyEngine _engine = new(PolicyRuleCatalog.BuiltIn);

    private static GovernanceRecord Record(string id, EnforcementLevel mode, string ruleId = "forbidden-path-change", params (string, string)[] parameters) =>
        TestRecords.Decision(id, scope: Gateway, rules: [Bind(ruleId, mode, parameters.Length == 0 ? [("paths", "src/Generated/**")] : parameters)]);

    private PolicyEvaluation Evaluate(PolicyContext context, params GovernanceRecord[] records) =>
        _engine.Evaluate(context, Resolve(records));

    [Fact]
    public void A_violation_becomes_a_finding_at_the_bound_mode_with_evidence()
    {
        var evaluation = Evaluate(Context(Added("src/Generated/A.cs", "x")), Record("ARCH-001", EnforcementLevel.Block));

        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal(PolicyFindingCodes.Violation, finding.Code);
        Assert.Equal(EnforcementLevel.Block, finding.Severity);
        Assert.Equal(["ARCH-001"], finding.GovernanceIds.AsEnumerable());
        Assert.Equal("src/Generated/A.cs", finding.Path);
        Assert.Contains(finding.Evidence, e => e.Source == "governance-record" && e.Id == "ARCH-001");
        var result = Assert.Single(evaluation.Results);
        Assert.Equal(PolicyOutcome.Violated, result.Outcome);
        Assert.StartsWith("sha256:", result.RuleHash, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_change_produces_a_pass_and_no_findings()
    {
        var evaluation = Evaluate(Context(Added("src/Other.cs", "x")), Record("ARCH-001", EnforcementLevel.Block));

        Assert.Empty(evaluation.Findings);
        Assert.Equal(PolicyOutcome.Passed, Assert.Single(evaluation.Results).Outcome);
    }

    [Fact]
    public void An_unregistered_rule_is_a_finding_never_a_pass()
    {
        var evaluation = Evaluate(Context(Added("a.cs", "x")), Record("ARCH-001", EnforcementLevel.Warn, "does-not-exist", ("x", "y")));

        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal(PolicyFindingCodes.RuleUnknown, finding.Code);
        Assert.True(finding.Severity >= EnforcementLevel.RequireReview);
        Assert.Equal(PolicyOutcome.Error, Assert.Single(evaluation.Results).Outcome);
    }

    [Fact]
    public void Misconfigured_parameters_are_a_finding_never_a_pass()
    {
        var evaluation = Evaluate(Context(Added("a.cs", "x")), Record("ARCH-001", EnforcementLevel.Info, "forbidden-path-change", ("bogus", "1")));

        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal(PolicyFindingCodes.RuleMisconfigured, finding.Code);
        Assert.True(finding.Severity >= EnforcementLevel.RequireReview);
    }

    [Fact]
    public void A_crashing_rule_is_isolated_and_reported_without_its_message()
    {
        var catalog = new PolicyRuleCatalog([new CrashingRule()]);
        var record = Record("ARCH-001", EnforcementLevel.Block, "crash", ("a", "b"));

        var evaluation = new PolicyEngine(catalog).Evaluate(Context(Added("a.cs", "x")), Resolve(record));

        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal(PolicyFindingCodes.RuleError, finding.Code);
        Assert.DoesNotContain("secret-in-exception", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_active_exception_waives_the_finding_but_keeps_it_visible()
    {
        var exception = TestRecords.Exception("EXC-001", ["ARCH-001"], Gateway, Now.AddDays(-1), Now.AddDays(30));

        var evaluation = Evaluate(Context(Added("src/Generated/A.cs", "x")), Record("ARCH-001", EnforcementLevel.Block), exception);

        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal("EXC-001", finding.WaivedBy);
    }

    [Fact]
    public void Output_does_not_depend_on_input_order()
    {
        var changes = new[] { Added("src/Generated/B.cs", "x"), Added("src/Generated/A.cs", "y"), Added("src/Ok.cs", "z") };
        var records = new[] { Record("ARCH-002", EnforcementLevel.Warn), Record("ARCH-001", EnforcementLevel.Block) };

        var forward = Evaluate(Context(changes), records);
        var reversed = Evaluate(Context([.. changes.Reverse()]), [.. records.Reverse()]);

        Assert.Equal(forward.Findings.Select(F), reversed.Findings.Select(F));
        Assert.Equal(forward.Results.Select(r => (r.RecordId, r.RuleHash, r.Outcome)), reversed.Results.Select(r => (r.RecordId, r.RuleHash, r.Outcome)));

        static string F(Finding f) => $"{f.Code}|{f.Severity}|{f.GovernanceIds[0]}|{f.Path}|{f.Message}";
    }

    [Fact]
    public void Rule_hash_depends_on_id_version_and_parameters_but_not_parameter_order()
    {
        KeyValuePair<string, string> P(string k, string v) => new(k, v);

        var hash = PolicyEngine.ComputeRuleHash("r", "1.0.0", [P("a", "1"), P("b", "2")]);

        Assert.Equal(hash, PolicyEngine.ComputeRuleHash("r", "1.0.0", [P("b", "2"), P("a", "1")]));
        Assert.NotEqual(hash, PolicyEngine.ComputeRuleHash("r", "1.0.1", [P("a", "1"), P("b", "2")]));
        Assert.NotEqual(hash, PolicyEngine.ComputeRuleHash("r", "1.0.0", [P("a", "12")]));
        Assert.NotEqual(PolicyEngine.ComputeRuleHash("r", "1", [P("ab", "c")]), PolicyEngine.ComputeRuleHash("r", "1", [P("a", "bc")]));
    }

    [Fact]
    public void Oversized_files_are_withheld_and_flagged_for_review()
    {
        var big = Added("src/Big.cs", new string('x', PolicyLimits.MaxFileCharacters + 1));

        var evaluation = Evaluate(Context(big), Record("ARCH-001", EnforcementLevel.Block));

        Assert.Contains(evaluation.Findings, f => f.Code == PolicyFindingCodes.FileTooLarge && f.Path == "src/Big.cs");
    }

    [Fact]
    public void The_catalog_rejects_duplicate_rule_ids()
    {
        Assert.Throws<ArgumentException>(() => new PolicyRuleCatalog([new CrashingRule(), new CrashingRule()]));
        Assert.Null(PolicyRuleCatalog.BuiltIn.Find("nope"));
        Assert.Equal(PolicyRuleCatalog.BuiltIn.Describe().Select(d => d.RuleId).Order(StringComparer.Ordinal), PolicyRuleCatalog.BuiltIn.Describe().Select(d => d.RuleId));
    }

    private sealed class CrashingRule : IPolicyRule
    {
        public string RuleId => "crash";

        public string Version => "1.0.0";

        public string Description => "always throws";

        public IReadOnlyDictionary<string, string> Parameters { get; } = ImmutableDictionary<string, string>.Empty;

        public IEnumerable<PolicyViolation> Evaluate(PolicyContext context, IReadOnlyDictionary<string, string> parameters) =>
            throw new InvalidOperationException("secret-in-exception");
    }
}
