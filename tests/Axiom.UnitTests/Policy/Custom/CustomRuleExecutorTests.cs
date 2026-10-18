using System.Text;
using Axiom.Application.Policy;
using Axiom.Application.Policy.Custom;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Custom;
using Axiom.UnitTests.Governance;
using static Axiom.UnitTests.Policy.PolicyTestKit;

namespace Axiom.UnitTests.Policy.Custom;

public class CustomRuleExecutorTests
{
    private const string NoTodo =
        """{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "forbid-regex", "pattern": "TODO" } ] }""";

    private sealed class FakeProcess(Func<RunnerProcessRequest, RunnerProcessResult> behavior) : ICustomRunnerProcess
    {
        public List<RunnerProcessRequest> Calls { get; } = [];

        public RunnerProcessResult Run(RunnerProcessRequest request)
        {
            Calls.Add(request);
            return behavior(request);
        }
    }

    private static RunnerProcessResult Ok(RunnerResponse response) =>
        new(RunnerProcessStatus.Exited, 0, RunnerProtocol.Serialize(response));

    private static RunnerProcessResult Raw(string text, int exit = 0) =>
        new(RunnerProcessStatus.Exited, exit, Encoding.UTF8.GetBytes(text));

    private static RunnerResponse Passed() => new(RunnerProtocol.Version, "passed", [], null, null);

    private static CustomRuleExecutor Executor(ICustomRunnerProcess process, string spec = NoTodo, CustomRunnerOptions? options = null) =>
        new(new CustomRuleDefinition("no-todo", "1.0.0", "No TODOs", spec), process, options ?? new CustomRunnerOptions());

    private static PolicyContext Ctx() => Context(Added("src/a.cs", "x // TODO"));

    private static List<PolicyViolation> Eval(CustomRuleExecutor executor, params (string, string)[] parameters) =>
        [.. executor.Evaluate(Ctx(), Args(parameters))];

    [Fact]
    public void Violations_from_the_runner_become_policy_violations()
    {
        var process = new FakeProcess(_ => Ok(new RunnerResponse(
            RunnerProtocol.Version, "violated", [new WireViolation("Found it.", "src/a.cs", 1, "matches TODO")], null, null)));

        var violation = Assert.Single(Eval(Executor(process)));

        Assert.Equal(new PolicyViolation("Found it.", "src/a.cs", 1, "matches TODO"), violation);
    }

    [Fact]
    public void The_request_carries_the_rule_the_changes_and_only_the_tree_paths()
    {
        var process = new FakeProcess(_ => Ok(Passed()));

        Eval(Executor(process));

        var call = Assert.Single(process.Calls);
        var request = RunnerProtocol.ParseRequest(call.Input);
        Assert.Equal("custom/no-todo", request.RuleId);
        Assert.Equal(NoTodo, request.Spec);
        Assert.Equal(["src/a.cs"], request.Context.TreePaths);
        Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);
        Assert.Equal(256 * 1024 * 1024, call.MaxMemoryBytes);
    }

    [Theory]
    [InlineData(RunnerProcessStatus.TimedOut, CustomRuleCodes.Timeout)]
    [InlineData(RunnerProcessStatus.OutputLimitExceeded, CustomRuleCodes.OutputTooLarge)]
    [InlineData(RunnerProcessStatus.MemoryLimitExceeded, CustomRuleCodes.MemoryLimit)]
    [InlineData(RunnerProcessStatus.LaunchFailed, CustomRuleCodes.LaunchFailed)]
    public void A_limit_breach_reported_by_the_launcher_is_an_error_with_a_stable_code(RunnerProcessStatus status, string code)
    {
        var process = new FakeProcess(_ => new RunnerProcessResult(status, -1, []));

        var ex = Assert.Throws<PolicyRuleExecutionException>(() => Eval(Executor(process)));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void A_crash_is_an_error()
    {
        var process = new FakeProcess(_ => Raw(string.Empty, exit: 134));

        Assert.Equal(CustomRuleCodes.Crash, Assert.Throws<PolicyRuleExecutionException>(() => Eval(Executor(process))).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"protocol":1,"outcome":"passed","violations":[{"message":"x"}]}""")]
    [InlineData("""{"protocol":1,"outcome":"violated","violations":[]}""")]
    [InlineData("""{"protocol":1,"outcome":"banana","violations":[]}""")]
    [InlineData("""{"protocol":2,"outcome":"passed","violations":[]}""")]
    [InlineData("""{"protocol":1,"outcome":"error","violations":[]}""")]
    [InlineData("""{"protocol":1,"outcome":"violated","violations":[{"message":"x","line":0}]}""")]
    public void Malformed_runner_output_is_an_error_never_a_pass(string output)
    {
        var process = new FakeProcess(_ => Raw(output));

        var ex = Assert.Throws<PolicyRuleExecutionException>(() => Eval(Executor(process)));

        Assert.Equal(CustomRuleCodes.MalformedOutput, ex.Code);
    }

    [Fact]
    public void Output_beyond_the_cap_is_rejected_even_if_the_launcher_returned_it()
    {
        var process = new FakeProcess(_ => Raw(new string(' ', 2_000) + RunnerJson()));

        var ex = Assert.Throws<PolicyRuleExecutionException>(() => Eval(Executor(process, options: new CustomRunnerOptions { MaxOutputBytes = 1_000 })));

        Assert.Equal(CustomRuleCodes.OutputTooLarge, ex.Code);

        static string RunnerJson() => """{"protocol":1,"outcome":"passed","violations":[]}""";
    }

    [Fact]
    public void Oversized_input_is_refused_without_launching_the_runner()
    {
        var process = new FakeProcess(_ => Ok(Passed()));
        var executor = Executor(process, options: new CustomRunnerOptions { MaxInputBytes = 100 });

        var ex = Assert.Throws<PolicyRuleExecutionException>(() => Eval(executor));

        Assert.Equal(CustomRuleCodes.InputTooLarge, ex.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void A_runner_reported_error_keeps_its_code_and_unknown_codes_become_crashes()
    {
        var limit = new FakeProcess(_ => Ok(new RunnerResponse(RunnerProtocol.Version, "error", [], CustomRuleCodes.StepBudgetExceeded, "x")));
        var odd = new FakeProcess(_ => Ok(new RunnerResponse(RunnerProtocol.Version, "error", [], "whatever", "x")));

        Assert.Equal(CustomRuleCodes.StepBudgetExceeded, Assert.Throws<PolicyRuleExecutionException>(() => Eval(Executor(limit))).Code);
        Assert.Equal(CustomRuleCodes.Crash, Assert.Throws<PolicyRuleExecutionException>(() => Eval(Executor(odd))).Code);
    }

    [Fact]
    public void An_invalid_definition_is_a_configuration_error_and_never_launches()
    {
        var process = new FakeProcess(_ => Ok(Passed()));
        var executor = Executor(process, spec: """{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "exec" } ] }""");

        Assert.Throws<PolicyParameterException>(() => Eval(executor));
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void Unknown_parameters_are_a_configuration_error_and_never_launch()
    {
        var process = new FakeProcess(_ => Ok(Passed()));

        Assert.Throws<PolicyParameterException>(() => Eval(Executor(process), ("bogus", "1")));
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void The_version_changes_when_the_definition_changes()
    {
        var a = Executor(new FakeProcess(_ => Ok(Passed())));
        var b = Executor(new FakeProcess(_ => Ok(Passed())), spec: NoTodo.Replace("TODO", "FIXME", StringComparison.Ordinal));

        Assert.StartsWith("1.0.0+spec.", a.Version, StringComparison.Ordinal);
        Assert.NotEqual(a.Version, b.Version);
        Assert.NotEqual(
            PolicyEngine.ComputeRuleHash(a.RuleId, a.Version, []),
            PolicyEngine.ComputeRuleHash(b.RuleId, b.Version, []));
    }

    [Fact]
    public void Custom_rules_cannot_be_registered_outside_their_namespace()
    {
        var shadow = new CustomRuleExecutor(new CustomRuleDefinition("x", "1.0.0", "d", NoTodo), new FakeProcess(_ => Ok(Passed())), new CustomRunnerOptions());
        var catalog = new CompositePolicyRuleCatalog(PolicyRuleCatalog.BuiltIn, [shadow]);

        Assert.NotNull(catalog.Find("custom/x"));
        Assert.NotNull(catalog.Find("forbidden-path-change"));
        Assert.Null(catalog.Find("custom/forbidden-path-change"));
        Assert.Contains(catalog.Describe(), d => d.RuleId == "custom/x");
        Assert.Throws<ArgumentException>(() => new CompositePolicyRuleCatalog(
            PolicyRuleCatalog.BuiltIn, [new StubRule("no-secrets")]));
    }

    [Fact]
    public void The_engine_reports_runner_failures_as_errors_with_the_reason_code()
    {
        var timedOut = new FakeProcess(_ => new RunnerProcessResult(RunnerProcessStatus.TimedOut, -1, []));
        var engine = new PolicyEngine(new CompositePolicyRuleCatalog(PolicyRuleCatalog.BuiltIn, [Executor(timedOut)]));
        var record = TestRecords.Decision(
            "ARCH-001",
            scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
            rules: [Bind("custom/no-todo", EnforcementLevel.Block)]);

        var evaluation = engine.Evaluate(Ctx(), Resolve(record));

        var result = Assert.Single(evaluation.Results);
        Assert.Equal(PolicyOutcome.Error, result.Outcome);
        Assert.Contains(CustomRuleCodes.Timeout, result.Error, StringComparison.Ordinal);
        var finding = Assert.Single(evaluation.Findings);
        Assert.Equal(PolicyFindingCodes.RuleError, finding.Code);
        Assert.True(finding.Severity >= EnforcementLevel.RequireReview);
    }

    [Fact]
    public void The_runner_core_turns_bad_requests_into_error_responses()
    {
        var response = RunnerProtocol.ParseResponse(CustomRuleRunnerCore.Run("garbage"u8));

        Assert.Equal("error", response.Outcome);
        Assert.Equal(CustomRuleCodes.BadRequest, response.Code);
    }

    [Fact]
    public void The_runner_core_round_trips_a_real_request()
    {
        var request = new RunnerRequest(
            RunnerProtocol.Version, "custom/no-todo", NoTodo, [], RunnerProtocol.ToWire(Ctx()));

        var response = RunnerProtocol.ParseResponse(CustomRuleRunnerCore.Run(RunnerProtocol.Serialize(request)));

        Assert.Equal("violated", response.Outcome);
        Assert.Equal("src/a.cs", Assert.Single(response.Violations).Path);
    }

    private sealed class StubRule(string id) : IPolicyRule
    {
        public string RuleId => id;

        public string Version => "1";

        public string Description => "";

        public IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>();

        public IEnumerable<PolicyViolation> Evaluate(PolicyContext context, IReadOnlyDictionary<string, string> parameters) => [];
    }
}
