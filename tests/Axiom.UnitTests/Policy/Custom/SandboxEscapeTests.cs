using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Axiom.Application.Policy;
using Axiom.Application.Policy.Custom;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Custom;
using Axiom.Infrastructure.Policy;
using Microsoft.Extensions.Configuration;
using Axiom.UnitTests.Governance;
using static Axiom.UnitTests.Policy.PolicyTestKit;

namespace Axiom.UnitTests.Policy.Custom;

/// <summary>
/// Adversarial suite for plan item 12.3: tries to break the custom-rule runner's limits and isolation. Every
/// attack must end as <see cref="PolicyOutcome.Error"/> with a stable reason (or a fail-closed violation), or as
/// a bounded result; none may end in a wrong pass, a hang or an unhandled exception. Tests that need a real
/// process use only local executables (no Docker). See docs/operations/sandbox-escape-test-report.md.
/// </summary>
public class SandboxEscapeTests
{
    private static readonly string RunnerDll = Path.Combine(AppContext.BaseDirectory, "Axiom.PolicyRunner.dll");

    private sealed class LoopbackRunner : ICustomRunnerProcess
    {
        public RunnerProcessResult Run(RunnerProcessRequest request) =>
            new(RunnerProcessStatus.Exited, 0, CustomRuleRunnerCore.Run(request.Input));
    }

    private static string Spec(string checksJson, string? selectJson = null, string? parametersJson = null) =>
        "{ \"schema\": \"axiom-custom-rule/v1\""
        + (parametersJson is null ? string.Empty : $", \"parameters\": {parametersJson}")
        + (selectJson is null ? string.Empty : $", \"select\": {selectJson}")
        + $", \"checks\": [ {checksJson} ] }}";

    private static string Check(string type, string? pattern = null, string? scope = null, string? path = null, string? op = null, string? value = null)
    {
        var fields = new List<string> { $"\"type\": {JsonSerializer.Serialize(type)}" };
        if (pattern is not null) { fields.Add($"\"pattern\": {JsonSerializer.Serialize(pattern)}"); }
        if (scope is not null) { fields.Add($"\"scope\": {JsonSerializer.Serialize(scope)}"); }
        if (path is not null) { fields.Add($"\"path\": {JsonSerializer.Serialize(path)}"); }
        if (op is not null) { fields.Add($"\"op\": {JsonSerializer.Serialize(op)}"); }
        if (value is not null) { fields.Add($"\"value\": {JsonSerializer.Serialize(value)}"); }
        return "{ " + string.Join(", ", fields) + " }";
    }

    private static WireChange Wire(string path, string content, string kind = "added") =>
        new(path, kind, null, content, null, [.. content.Split('\n').Select((t, i) => new WireAddedLine(i + 1, t))]);

    private static byte[] Req(string spec, IEnumerable<WireChange> changes, IEnumerable<string>? tree = null, Dictionary<string, string>? parameters = null)
    {
        var list = changes.ToList();
        return RunnerProtocol.Serialize(new RunnerRequest(
            RunnerProtocol.Version,
            "custom/t",
            spec,
            parameters ?? [],
            new WireContext("gateway", list, [.. tree ?? list.Select(c => c.Path)])));
    }

    private static RunnerResponse Core(string spec, IEnumerable<WireChange> changes, IEnumerable<string>? tree = null, Dictionary<string, string>? parameters = null, long? budget = null)
    {
        var bytes = Req(spec, changes, tree, parameters);
        return RunnerProtocol.ParseResponse(budget is null ? CustomRuleRunnerCore.Run(bytes) : CustomRuleRunnerCore.Run(bytes, budget.Value));
    }

    private static void AssertError(RunnerResponse response, string code)
    {
        Assert.Equal("error", response.Outcome);
        Assert.Equal(code, response.Code);
        Assert.Empty(response.Violations);
    }

    private static PolicyRunResult Engine(ICustomRunnerProcess process, string spec, PolicyContext context, CustomRunnerOptions? options = null, params (string, string)[] parameters)
    {
        var executor = new CustomRuleExecutor(new CustomRuleDefinition("t", "1.0.0", "d", spec), process, options ?? new CustomRunnerOptions());
        var engine = new PolicyEngine(new CompositePolicyRuleCatalog(PolicyRuleCatalog.BuiltIn, [executor]));
        var record = TestRecords.Decision(
            "ARCH-001",
            scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
            rules: [Bind("custom/t", EnforcementLevel.Block, parameters)]);
        return Assert.Single(engine.Evaluate(context, Resolve(record)).Results);
    }

    // ---------------------------------------------------------------- regular expressions

    [Theory]
    [InlineData("(?=a)b")]
    [InlineData("(?!a)b")]
    [InlineData("(?<=a)b")]
    [InlineData("(?<!a)b")]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?<n>a)\k<n>")]
    [InlineData("(?>a+)b")]
    [InlineData("(?(1)a|b)")]
    [InlineData("(?r:a)")]
    [InlineData("(")]
    [InlineData("[")]
    [InlineData("*a")]
    [InlineData(@"\")]
    [InlineData("a{2,1}")]
    public void Lookaround_backreference_and_malformed_patterns_are_rejected_not_executed(string pattern)
    {
        foreach (var type in new[] { "forbid-regex", "require-regex" })
        {
            var response = Core(Spec(Check(type, pattern)), [Wire("src/a.cs", "aab")]);

            AssertError(response, CustomRuleCodes.InvalidRule);
        }
    }

    [Fact]
    public void A_rejected_pattern_is_a_policy_error_with_no_violations_in_the_engine()
    {
        var result = Engine(new LoopbackRunner(), Spec(Check("forbid-regex", "(?=x)y")), Context(Added("src/a.cs", "y")));

        Assert.Equal(PolicyOutcome.Error, result.Outcome);
        Assert.Empty(result.Violations);
    }

    [Theory]
    [InlineData("(a+)+$")]
    [InlineData("(a|aa)+$")]
    [InlineData("(.*)*x")]
    [InlineData("(a*)*b")]
    [InlineData("^(([a-z])+.)+[A-Z]([a-z])+$")]
    [InlineData("(x+x+)+y")]
    public void Catastrophic_backtracking_patterns_run_in_linear_time(string pattern)
    {
        var hostile = new string('a', 200_000) + "!";
        var watch = Stopwatch.StartNew();

        foreach (var type in new[] { "forbid-regex", "require-regex" })
        {
            var response = Core(Spec(Check(type, pattern, scope: type == "forbid-regex" ? "content" : null)), [Wire("src/a.cs", hostile)]);

            Assert.Contains(response.Outcome, new[] { "passed", "violated" });
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("a{1000000}")]
    [InlineData("a{0,2147483647}")]
    [InlineData("[a-z]{1,2147483647}")]
    [InlineData(".{100000}")]
    [InlineData("(a{1000}){1000}")]
    [InlineData("((a{1000}){1000}){1000}")]
    [InlineData("(((a{100}){100}){100}){100}")]
    [InlineData("(a|b|c|d|e|f|g|h){5000}")]
    public void Huge_repetition_counts_end_bounded_in_a_real_process(string pattern)
    {
        // Pattern construction is not covered by the regex match timeout: it must be survivable by the process limits.
        var spec = Spec(Check("forbid-regex", pattern, scope: "content"));
        var launcher = new ProcessCustomRunnerProcess("dotnet", [RunnerDll]);
        var options = new CustomRunnerOptions { TimeoutSeconds = 10 };
        var watch = Stopwatch.StartNew();

        var result = Engine(new RealRunner(launcher), spec, Context(Added("src/a.cs", "abc")), options);

        // "abc" cannot match any of these, so a pass is correct only if the pattern compiled; the point is no hang or crash of the host.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
        Assert.Contains(result.Outcome, new[] { PolicyOutcome.Passed, PolicyOutcome.Violated, PolicyOutcome.Error });
    }

    private sealed class RealRunner(ProcessCustomRunnerProcess inner) : ICustomRunnerProcess
    {
        public RunnerProcessResult Run(RunnerProcessRequest request) => inner.Run(request);
    }

    [Fact]
    public void Parameter_substitution_cannot_grow_a_pattern_beyond_the_pattern_cap()
    {
        var spec = Spec(Check("forbid-regex", "{{p}}{{p}}{{p}}"), parametersJson: "{ \"p\": \"x\" }");
        var parameters = new Dictionary<string, string> { ["p"] = new string('x', 100_000) };

        var response = Core(spec, [Wire("src/a.cs", "x")], parameters: parameters);

        AssertError(response, CustomRuleCodes.InvalidParameters);
    }

    [Fact]
    public void Parameter_values_are_substituted_once_and_never_re_expanded()
    {
        var spec = Spec(Check("forbid-regex", "{{a}}"), parametersJson: "{ \"a\": \"x\", \"b\": \"SECRET\" }");

        var response = Core(spec, [Wire("src/a.cs", "{{b}} SECRET")], parameters: new() { ["a"] = "{{b}}" });

        Assert.Equal("violated", response.Outcome);
        Assert.Equal("src/a.cs", Assert.Single(response.Violations).Path);
    }

    [Theory]
    [InlineData("{{")]
    [InlineData("{{}}")]
    [InlineData("{{{{a}}}}")]
    [InlineData("{{ nope }}")]
    public void Malformed_or_undeclared_placeholders_never_pass(string pattern)
    {
        var response = Core(Spec(Check("forbid-regex", pattern), parametersJson: "{ \"a\": \"x\" }"), [Wire("src/a.cs", "x")]);

        // An unterminated "{{" is a literal; the others reference undeclared parameters. Never a silent pass of a wrong rule.
        if (pattern == "{{")
        {
            Assert.Contains(response.Outcome, new[] { "passed", "violated", "error" });
        }
        else
        {
            AssertError(response, CustomRuleCodes.InvalidRule);
        }
    }

    // ---------------------------------------------------------------- rule language bounds

    [Fact]
    public void An_oversized_or_deeply_nested_definition_is_rejected()
    {
        var oversized = Spec(Check("forbid-regex", "x")).Replace("\"checks\"", $"\"description\": \"{new string('d', 70_000)}\", \"checks\"", StringComparison.Ordinal);
        var nested = "{\"schema\":\"axiom-custom-rule/v1\",\"description\":" + new string('[', 500) + new string(']', 500) + ",\"checks\":[]}";

        AssertError(Core(oversized, [Wire("a", "x")]), CustomRuleCodes.InvalidRule);
        AssertError(Core(nested, [Wire("a", "x")]), CustomRuleCodes.InvalidRule);
    }

    [Theory]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "forbid-regex", "pattern": "x", "exec": "calc" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "assembly": "evil.dll", "checks": [ { "type": "forbid-regex", "pattern": "x" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v2", "checks": [ { "type": "forbid-regex", "pattern": "x" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "script", "pattern": "x" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "forbid-regex", "pattern": "x" }, { "type": "forbid-regex", "pattern": "x", "pattern": "y" } ] }""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void Unknown_properties_and_check_types_are_errors_never_ignored(string spec)
    {
        var response = Core(spec, [Wire("src/a.cs", "x")]);

        Assert.Equal("error", response.Outcome);
        Assert.Equal(CustomRuleCodes.InvalidRule, response.Code);
    }

    [Fact]
    public void More_than_the_maximum_number_of_checks_is_rejected()
    {
        var checks = string.Join(",", Enumerable.Repeat(Check("forbid-regex", "x"), CustomRuleSpec.MaxChecks + 1));

        AssertError(Core(Spec(checks), [Wire("src/a.cs", "x")]), CustomRuleCodes.InvalidRule);
    }

    // ---------------------------------------------------------------- json path

    [Theory]
    [InlineData("$..a")]
    [InlineData("$[?(@.a)]")]
    [InlineData("$[0:5]")]
    [InlineData("$[-1]")]
    [InlineData("$[99999999999]")]
    [InlineData("$['a'")]
    [InlineData("a.b")]
    [InlineData("$.")]
    [InlineData("$.a.b.c.d.e.f.g.h.i.j.k.l.m.n.o.p.q.r.s.t.u.v.w.x.y.z.a.b.c.d.e.f.g.h")]
    public void Unsupported_json_path_syntax_is_rejected(string path)
    {
        var response = Core(Spec(Check("json-path", path: path, op: "exists")), [Wire("c.json", "{}")]);

        AssertError(response, CustomRuleCodes.InvalidRule);
    }

    [Fact]
    public void A_json_path_smuggled_in_through_a_parameter_is_validated_at_run_time()
    {
        var spec = Spec(Check("json-path", path: "{{p}}", op: "exists"), parametersJson: "{ \"p\": \"$.a\" }");

        AssertError(Core(spec, [Wire("c.json", "{\"a\":1}")], parameters: new() { ["p"] = "$..a" }), CustomRuleCodes.InvalidRule);
        Assert.Equal("passed", Core(spec, [Wire("c.json", "{\"a\":1}")]).Outcome);
    }

    [Fact]
    public void Deeply_nested_json_content_is_a_violation_never_a_pass_or_a_crash()
    {
        var deep = new string('[', 100_000) + new string(']', 100_000);
        var spec = Spec(Check("json-path", path: "$[0]", op: "absent"));

        var response = Core(spec, [Wire("c.json", deep)]);

        Assert.Equal("violated", response.Outcome);
        Assert.Contains("not valid JSON", Assert.Single(response.Violations).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wildcard_chains_over_wide_documents_stay_inside_the_step_budget()
    {
        var wide = "[" + string.Join(",", Enumerable.Repeat("[" + string.Join(",", Enumerable.Range(0, 200)) + "]", 2_000)) + "]";
        var path = "$" + string.Concat(Enumerable.Repeat("[*]", 32));
        var watch = Stopwatch.StartNew();

        var response = Core(Spec(Check("json-path", path: path, op: "exists")), [Wire("c.json", wide)], budget: 100_000);

        AssertError(response, CustomRuleCodes.StepBudgetExceeded);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    // ---------------------------------------------------------------- budgets

    [Fact]
    public void The_step_budget_stops_work_on_large_content()
    {
        var content = string.Join("\n", Enumerable.Repeat("some content line", 5_000));

        var response = Core(Spec(Check("forbid-regex", "zzz", scope: "content")), [Wire("src/a.cs", content)], budget: 1_000);

        AssertError(response, CustomRuleCodes.StepBudgetExceeded);
    }

    [Fact]
    public void A_violation_flood_is_capped_and_the_cap_is_independent_of_input_order()
    {
        var files = Enumerable.Range(0, 3_000).Select(i => Wire($"src/f{i:D5}.cs", "TODO\nTODO\nTODO")).ToList();
        var spec = Spec(string.Join(",", Enumerable.Repeat(Check("forbid-regex", "TODO"), 5)));

        var forward = Core(spec, files);
        files.Reverse();
        var backward = Core(spec, files);

        Assert.Equal("violated", forward.Outcome);
        Assert.True(forward.Violations.Count <= CustomRuleEvaluator.MaxViolations);
        Assert.Equal(forward.Violations.Select(v => (v.Path, v.Line)), backward.Violations.Select(v => (v.Path, v.Line)));
    }

    [Fact]
    public void A_violation_flood_through_the_engine_is_capped_with_an_omission_notice()
    {
        var content = string.Join("\n", Enumerable.Repeat("TODO", 2_000));
        var result = Engine(new LoopbackRunner(), Spec(Check("forbid-regex", "TODO")), Context(Added("src/a.cs", content)));

        Assert.Equal(PolicyOutcome.Violated, result.Outcome);
        Assert.True(result.Violations.Length <= PolicyLimits.MaxViolationsPerExecution + 1);
    }

    [Fact]
    public void Violation_fields_are_truncated_to_the_field_cap()
    {
        var spec = Spec(
            Check("forbid-path").Replace("}", ", \"message\": \"{{p}}\" }", StringComparison.Ordinal),
            parametersJson: "{ \"p\": \"m\" }");

        // 900 characters are accepted by the field rules; the response truncation is the second line of defence.
        var response = Core(spec, [Wire("src/a.cs", "x")], parameters: new() { ["p"] = new string('m', 900) });

        Assert.True(Assert.Single(response.Violations).Message.Length <= RunnerProtocol.MaxFieldCharacters);
    }

    [Fact]
    public void Giant_input_is_refused_before_a_process_is_started()
    {
        var calls = 0;
        var counting = new CountingRunner(() => calls++);
        // Each file stays under the per-file content cap (which the engine enforces itself); together they exceed the request cap.
        var files = Enumerable.Range(0, 6).Select(i => Added($"src/big{i}.cs", new string('x', 900_000))).ToArray();

        var result = Engine(counting, Spec(Check("forbid-regex", "x")), Context(files));

        Assert.Equal(PolicyOutcome.Error, result.Outcome);
        Assert.Contains(CustomRuleCodes.InputTooLarge, result.Error, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    private sealed class CountingRunner(Action onRun) : ICustomRunnerProcess
    {
        public RunnerProcessResult Run(RunnerProcessRequest request)
        {
            onRun();
            return new RunnerProcessResult(RunnerProcessStatus.Exited, 0, []);
        }
    }

    [Fact]
    public void The_real_runner_refuses_more_stdin_than_it_was_told_to_accept()
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(RunnerDll);
        info.Environment["AXIOM_RUNNER_MAX_INPUT_BYTES"] = "100";
        using var process = Process.Start(info)!;
        try
        {
            process.StandardInput.BaseStream.Write(new byte[50_000]);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The runner exits as soon as the cap is crossed; the rest of the write is refused.
        }

        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(3, process.ExitCode);
        Assert.Empty(process.StandardOutput.ReadToEnd());
    }

    [Fact]
    public void A_result_above_the_output_cap_becomes_an_output_too_large_error_in_the_real_runner()
    {
        var content = string.Join("\n", Enumerable.Repeat("TODO", 400));
        var spec = Spec(Check("forbid-regex", "TODO"));
        var options = new CustomRunnerOptions { MaxOutputBytes = 2_000 };

        var result = Engine(new RealRunner(new ProcessCustomRunnerProcess("dotnet", [RunnerDll])), spec, Context(Added("src/a.cs", content)), options);

        Assert.Equal(PolicyOutcome.Error, result.Outcome);
        Assert.Contains(CustomRuleCodes.OutputTooLarge, result.Error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- paths and selectors

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("..\\..\\secrets")]
    [InlineData("//server/share/x")]
    public void Traversal_and_absolute_selectors_are_only_ever_matched_against_the_tree_never_the_filesystem(string selector)
    {
        var requireFile = Core(Spec(Check("require-file", path: selector)), [Wire("src/a.cs", "x")], tree: ["src/a.cs"]);
        var forbidPath = Core(Spec(Check("forbid-path"), selectJson: $"{{ \"paths\": [{JsonSerializer.Serialize(selector)}] }}"), [Wire("src/a.cs", "x")]);

        // The file exists on this machine in some cases (e.g. win.ini), yet the rule cannot see it: missing in the tree is a violation.
        Assert.Equal("violated", requireFile.Outcome);
        Assert.Equal("passed", forbidPath.Outcome);
    }

    [Theory]
    [InlineData("src/../secrets/key.pem")]
    [InlineData("src/..\\secrets\\key.pem")]
    [InlineData("../outside.txt")]
    [InlineData("secrets/key\0.pem")]
    public void Paths_that_climb_out_of_their_directory_are_a_bad_request_not_a_selector_bypass(string path)
    {
        var spec = Spec(Check("forbid-path"), selectJson: "{ \"paths\": [\"secrets/**\"] }");

        AssertError(Core(spec, [Wire(path, "x")]), CustomRuleCodes.BadRequest);
        AssertError(Core(spec, [Wire("src/a.cs", "x")], tree: ["src/a.cs", path]), CustomRuleCodes.BadRequest);
    }

    [Theory]
    [InlineData("secrets/key.pem")]
    [InlineData("secrets\\key.pem")]
    [InlineData("./secrets/key.pem")]
    [InlineData("//secrets//key.pem")]
    [InlineData("secrets/./key.pem")]
    [InlineData("  secrets/key.pem  ")]
    public void Separator_and_dot_tricks_still_hit_a_forbid_path_selector(string path)
    {
        var response = Core(Spec(Check("forbid-path"), selectJson: "{ \"paths\": [\"secrets/**\"] }"), [Wire(path, "x")]);

        Assert.Equal("violated", response.Outcome);
    }

    [Theory]
    [InlineData("secrets/key\u200b.pem")]
    [InlineData("secrets/\u202egnp.pem")]
    [InlineData("secrets\uff0fkey.pem")]
    public void Look_alike_unicode_paths_are_distinct_paths_and_never_crash_the_rule(string path)
    {
        var response = Core(Spec(Check("forbid-path"), selectJson: "{ \"paths\": [\"secrets/**\"] }"), [Wire(path, "x")]);

        // Documented: only separators and dot segments are folded (no Unicode normalization; see the report). Invisible and look-alike characters are different file names.
        Assert.Contains(response.Outcome, new[] { "passed", "violated" });
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("./")]
    public void Degenerate_selectors_are_invalid_rules_not_runner_crashes(string selector)
    {
        var spec = Spec(Check("forbid-path"), selectJson: $"{{ \"paths\": [{JsonSerializer.Serialize(selector)}] }}");

        AssertError(Core(spec, [Wire("src/a.cs", "x")]), CustomRuleCodes.InvalidRule);
        AssertError(Core(Spec(Check("require-file", path: selector)), [Wire("src/a.cs", "x")]), CustomRuleCodes.InvalidRule);
    }

    [Fact]
    public void Pathological_globs_terminate()
    {
        var glob = string.Concat(Enumerable.Repeat("**/a*a*a*a*", 8)) + "b";
        var path = string.Join("/", Enumerable.Repeat(new string('a', 60), 30));
        var watch = Stopwatch.StartNew();

        var response = Core(Spec(Check("forbid-path"), selectJson: $"{{ \"paths\": [{JsonSerializer.Serialize(glob)}] }}"), [Wire(path, "x")]);

        Assert.Contains(response.Outcome, new[] { "passed", "error" });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    // ---------------------------------------------------------------- rule ids and namespace

    [Theory]
    [InlineData("Custom/t")]
    [InlineData("custom\\t")]
    [InlineData(" custom/t")]
    [InlineData("custom/t ")]
    [InlineData("custom/t\u200b")]
    [InlineData("\uff43ustom/t")]
    [InlineData("custom/./t")]
    [InlineData("custom//t")]
    [InlineData("custom/forbidden-path-change")]
    [InlineData("forbidden-path-change/../custom/t")]
    public void Look_alike_rule_ids_resolve_to_nothing_and_the_binding_is_an_error(string ruleId)
    {
        var executor = new CustomRuleExecutor(new CustomRuleDefinition("t", "1.0.0", "d", Spec(Check("forbid-regex", "x"))), new LoopbackRunner(), new CustomRunnerOptions());
        var catalog = new CompositePolicyRuleCatalog(PolicyRuleCatalog.BuiltIn, [executor]);
        var engine = new PolicyEngine(catalog);
        var record = TestRecords.Decision("ARCH-001", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])), rules: [Bind(ruleId, EnforcementLevel.Block)]);

        Assert.Null(catalog.Find(ruleId));
        var result = Assert.Single(engine.Evaluate(Context(Added("src/a.cs", "x")), Resolve(record)).Results);
        Assert.Equal(PolicyOutcome.Error, result.Outcome);
    }

    [Fact]
    public void A_custom_rule_cannot_replace_a_builtin_or_be_registered_twice()
    {
        var process = new LoopbackRunner();
        var builtInId = PolicyRuleCatalog.BuiltIn.Describe()[0].RuleId;
        var a = new CustomRuleExecutor(new CustomRuleDefinition("t", "1", "d", Spec(Check("forbid-regex", "x"))), process, new CustomRunnerOptions());
        var b = new CustomRuleExecutor(new CustomRuleDefinition("t", "2", "d", Spec(Check("forbid-regex", "y"))), process, new CustomRunnerOptions());
        var catalog = new CompositePolicyRuleCatalog(PolicyRuleCatalog.BuiltIn, [a]);

        Assert.Same(PolicyRuleCatalog.BuiltIn.Find(builtInId), catalog.Find(builtInId));
        Assert.Throws<ArgumentException>(() => new CompositePolicyRuleCatalog(PolicyRuleCatalog.BuiltIn, [a, b]));
    }

    [Theory]
    [InlineData("caf\u00e9")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a\u200b")]
    [InlineData("\uff41")]
    [InlineData("")]
    [InlineData(" ")]
    public void Configured_rule_names_outside_the_ascii_allow_list_are_refused_at_startup(string name)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Axiom:Policy:CustomRunner:Enabled"] = "true",
                ["Axiom:Policy:CustomRunner:Rules:0:Name"] = name,
                ["Axiom:Policy:CustomRunner:Rules:0:Spec"] = Spec(Check("forbid-regex", "x")),
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => { _ = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCustomPolicyRunner(configuration); });
    }

    [Fact]
    public void The_runner_is_off_by_default_and_registers_no_custom_rule()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCustomPolicyRunner(configuration);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPolicyRuleCatalog));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ICustomRunnerProcess));
    }

    // ---------------------------------------------------------------- protocol frames

    private static readonly byte[] ValidRequest = Req(Spec(Check("forbid-regex", "TODO")), [Wire("src/a.cs", "x // TODO")]);

    private static RunnerResponse RunRaw(ReadOnlySpan<byte> bytes) => RunnerProtocol.ParseResponse(CustomRuleRunnerCore.Run(bytes));

    [Fact]
    public void Every_truncation_of_a_request_is_a_bad_request_error()
    {
        Assert.Equal("violated", RunRaw(ValidRequest).Outcome);

        for (var length = 0; length < ValidRequest.Length; length++)
        {
            AssertError(RunRaw(ValidRequest.AsSpan(0, length)), CustomRuleCodes.BadRequest);
        }
    }

    [Fact]
    public void Corrupting_any_single_request_byte_never_throws_and_never_returns_a_malformed_response()
    {
        var random = new Random(20261018);
        for (var i = 0; i < 1_500; i++)
        {
            var mutated = (byte[])ValidRequest.Clone();
            mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            var response = RunRaw(mutated);

            Assert.Contains(response.Outcome, new[] { "passed", "violated", "error" });
            if (response.Outcome == "error")
            {
                Assert.StartsWith(CustomRuleCodes.Prefix, response.Code, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("""{"protocol":1}""")]
    [InlineData("""{"protocol":2,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":null,"parameters":{},"context":{"repository":"r","changes":[],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":null,"context":{"repository":"r","changes":[],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[null],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[],"treePaths":[null]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[{"path":null,"kind":"added","addedLines":[]}],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[{"path":"a","kind":"added","addedLines":[null]}],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[{"path":"a","kind":"added","addedLines":[{"number":99999999999,"text":"x"}]}],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[{"path":"a","kind":"symlink","addedLines":[]}],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[{"path":"a","kind":"added","addedLines":[{"number":1,"text":null}]}],"treePaths":[]}}""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[],"treePaths":[]}} trailing""")]
    [InlineData("""{"protocol":1,"ruleId":"r","spec":"{}","parameters":{},"context":{"repository":"r","changes":[],"treePaths":[]}}{"protocol":1}""")]
    public void Malformed_request_shapes_are_bad_requests(string json)
    {
        var response = RunRaw(Encoding.UTF8.GetBytes(json));

        Assert.Equal("error", response.Outcome);
        Assert.Contains(response.Code, new[] { CustomRuleCodes.BadRequest, CustomRuleCodes.InvalidRule });
    }

    [Fact]
    public void Byte_order_marks_invalid_utf8_and_deep_nesting_are_bad_requests()
    {
        AssertError(RunRaw([.. Encoding.UTF8.GetPreamble(), .. ValidRequest]), CustomRuleCodes.BadRequest);
        AssertError(RunRaw([.. "{\"protocol\":1,\"ruleId\":\""u8, 0xFF, 0xFE, .. "\"}"u8]), CustomRuleCodes.BadRequest);
        AssertError(RunRaw(Encoding.UTF8.GetBytes(new string('[', 5_000) + new string(']', 5_000))), CustomRuleCodes.BadRequest);
    }

    [Fact]
    public void Duplicate_keys_in_a_request_are_refused_so_the_runner_cannot_see_a_different_rule_than_the_parent_hashed()
    {
        var json = """{"protocol":1,"ruleId":"r","spec":"{}","spec":"{\"schema\":\"axiom-custom-rule/v1\",\"checks\":[{\"type\":\"forbid-path\"}]}","parameters":{},"context":{"repository":"r","changes":[{"path":"a","kind":"added","addedLines":[]}],"treePaths":["a"]}}""";

        var response = RunRaw(Encoding.UTF8.GetBytes(json));

        AssertError(response, CustomRuleCodes.BadRequest);
    }

    [Fact]
    public void Every_truncation_of_a_runner_response_is_malformed_output()
    {
        var valid = RunnerProtocol.Serialize(new RunnerResponse(
            RunnerProtocol.Version, "violated", [new WireViolation("m", "p", 1, "e")], null, null));
        Assert.Equal("violated", RunnerProtocol.ParseResponse(valid).Outcome);

        for (var length = 0; length < valid.Length; length++)
        {
            var ex = Assert.Throws<PolicyRuleExecutionException>(() => RunnerProtocol.ParseResponse(valid.AsSpan(0, length)));
            Assert.Equal(CustomRuleCodes.MalformedOutput, ex.Code);
        }
    }

    [Theory]
    [InlineData("""{"protocol":1,"outcome":"passed","violations":[]}{"protocol":1,"outcome":"passed","violations":[]}""")]
    [InlineData("""{"protocol":1,"outcome":"passed","violations":[]} garbage""")]
    [InlineData("""{"protocol":1,"outcome":"PASSED","violations":[]}""")]
    [InlineData("""{"protocol":1,"outcome":"passed","violations":null}""")]
    [InlineData("""{"protocol":1,"outcome":"violated","violations":[null]}""")]
    [InlineData("""{"protocol":1,"outcome":"violated","violations":[{"message":null}]}""")]
    [InlineData("""{"protocol":1,"outcome":"violated","violations":[{"message":"x","line":-5}]}""")]
    [InlineData("""{"protocol":1,"outcome":"error","violations":[],"code":""}""")]
    public void Tampered_runner_responses_are_malformed_output(string json)
    {
        var ex = Assert.Throws<PolicyRuleExecutionException>(() => RunnerProtocol.ParseResponse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(CustomRuleCodes.MalformedOutput, ex.Code);
    }

    [Fact]
    public void A_response_with_oversized_fields_or_too_many_violations_is_malformed_output()
    {
        var longText = new string('x', RunnerProtocol.MaxFieldCharacters + 1);
        var tooMany = Enumerable.Range(0, CustomRuleEvaluator.MaxViolations + 2).Select(i => new WireViolation($"m{i}", null, null, null)).ToList();

        foreach (var bad in new[]
        {
            new RunnerResponse(1, "violated", [new WireViolation(longText, null, null, null)], null, null),
            new RunnerResponse(1, "violated", [new WireViolation("m", longText, null, null)], null, null),
            new RunnerResponse(1, "violated", [new WireViolation("m", null, null, longText)], null, null),
            new RunnerResponse(1, "violated", tooMany, null, null),
        })
        {
            var ex = Assert.Throws<PolicyRuleExecutionException>(() => RunnerProtocol.ParseResponse(RunnerProtocol.Serialize(bad)));
            Assert.Equal(CustomRuleCodes.MalformedOutput, ex.Code);
        }
    }

    [Fact]
    public void A_runner_that_claims_to_pass_while_exiting_nonzero_or_emitting_nothing_is_an_error()
    {
        var passedJson = Encoding.UTF8.GetBytes("""{"protocol":1,"outcome":"passed","violations":[]}""");

        foreach (var result in new[]
        {
            new RunnerProcessResult(RunnerProcessStatus.Exited, 1, passedJson),
            new RunnerProcessResult(RunnerProcessStatus.Exited, 0, []),
            new RunnerProcessResult(RunnerProcessStatus.TimedOut, 0, passedJson),
            new RunnerProcessResult(RunnerProcessStatus.OutputLimitExceeded, 0, passedJson),
            new RunnerProcessResult(RunnerProcessStatus.MemoryLimitExceeded, 0, passedJson),
        })
        {
            var outcome = Engine(new FixedRunner(result), Spec(Check("forbid-regex", "x")), Context(Added("src/a.cs", "x")));

            Assert.Equal(PolicyOutcome.Error, outcome.Outcome);
        }
    }

    private sealed class FixedRunner(RunnerProcessResult result) : ICustomRunnerProcess
    {
        public RunnerProcessResult Run(RunnerProcessRequest request) => result;
    }

    [Fact]
    public void A_runner_error_message_that_quotes_content_is_not_recorded_for_limit_failures()
    {
        var hostile = new RunnerResponse(1, "error", [], CustomRuleCodes.StepBudgetExceeded, "api_key=SUPERSECRET");

        var result = Engine(new FixedRunner(new RunnerProcessResult(RunnerProcessStatus.Exited, 0, RunnerProtocol.Serialize(hostile))),
            Spec(Check("forbid-regex", "x")), Context(Added("src/a.cs", "x")));

        Assert.Equal(PolicyOutcome.Error, result.Outcome);
        Assert.DoesNotContain("SUPERSECRET", result.Error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- process isolation

    private static ProcessCustomRunnerProcess Shell(string script) =>
        OperatingSystem.IsWindows()
            ? new ProcessCustomRunnerProcess("powershell", ["-NoProfile", "-NonInteractive", "-Command", script])
            : new ProcessCustomRunnerProcess("sh", ["-c", script]);

    private static RunnerProcessRequest Limits(double seconds = 30, long memory = 256L * 1024 * 1024) =>
        new([], TimeSpan.FromSeconds(seconds), 256 * 1024, memory);

    [Fact]
    public void The_child_sees_no_parent_secrets_or_credential_shaped_variables()
    {
        var planted = new Dictionary<string, string>
        {
            ["AXIOM_SANDBOX_TEST_SECRET"] = "leak-me-1",
            ["ConnectionStrings__Default"] = "Host=db;Password=leak-me-2",
            ["OPENAI_API_KEY"] = "leak-me-3",
            ["GITHUB_TOKEN"] = "leak-me-4",
            ["AWS_SECRET_ACCESS_KEY"] = "leak-me-5",
            ["AXIOM_SANDBOX_PASSWORD"] = "leak-me-6",
            ["KUBERNETES_SERVICE_HOST"] = "10.0.0.1",
            ["Axiom__Api__SigningKey"] = "leak-me-7",
        };
        foreach (var (name, value) in planted)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            var script = OperatingSystem.IsWindows()
                ? "Get-ChildItem Env: | ForEach-Object { $_.Name + '=' + $_.Value }"
                : "env";

            var result = Shell(script).Run(Limits());

            Assert.Equal(0, result.ExitCode);
            var dump = Encoding.UTF8.GetString(result.Output);
            Assert.NotEmpty(dump);
            foreach (var (name, value) in planted)
            {
                Assert.DoesNotContain(name, dump, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(value, dump, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("leak-me", dump, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var name in planted.Keys)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public void The_child_runs_in_the_temp_directory_not_the_application_directory()
    {
        var script = OperatingSystem.IsWindows() ? "[Console]::Out.Write((Get-Location).Path)" : "pwd";

        var result = Shell(script).Run(Limits());

        var directory = Encoding.UTF8.GetString(result.Output).Trim();
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Stderr_noise_is_discarded_and_never_reaches_the_result()
    {
        var script = OperatingSystem.IsWindows()
            ? "[Console]::Error.Write('api_key=STDERRSECRET'); exit 0"
            : "echo api_key=STDERRSECRET 1>&2";

        var result = Shell(script).Run(Limits());

        Assert.DoesNotContain("STDERRSECRET", Encoding.UTF8.GetString(result.Output), StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_that_ignores_stdin_and_never_exits_is_killed_at_the_timeout()
    {
        var watch = Stopwatch.StartNew();

        var result = Shell(OperatingSystem.IsWindows() ? "while ($true) { }" : "while :; do :; done").Run(
            new RunnerProcessRequest(new byte[8 * 1024 * 1024], TimeSpan.FromSeconds(1), 256 * 1024, 256L * 1024 * 1024));

        Assert.Equal(RunnerProcessStatus.TimedOut, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_timeout_kills_the_whole_process_tree_including_grandchildren()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"axiom-sandbox-{Guid.NewGuid():N}.pid");
        try
        {
            var script = OperatingSystem.IsWindows()
                ? $"$p = Start-Process -PassThru -WindowStyle Hidden powershell -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120'; Set-Content -Path '{marker}' -Value $p.Id; Start-Sleep -Seconds 120"
                : $"sleep 120 & echo $! > '{marker}'; sleep 120";

            var result = Shell(script).Run(Limits(seconds: 6));

            Assert.Equal(RunnerProcessStatus.TimedOut, result.Status);
            Assert.True(File.Exists(marker), "the child did not get far enough to start a grandchild; raise the timeout");
            var pid = int.Parse(File.ReadAllText(marker).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (IsRunning(pid) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            Assert.False(IsRunning(pid), $"grandchild {pid} survived the timeout kill");
        }
        finally
        {
            File.Delete(marker);
        }

        static bool IsRunning(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    [Fact]
    public void A_child_that_exhausts_memory_is_killed_with_the_memory_status()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The portable shells cannot allocate on demand; the Windows run covers the supervisor logic.
        }

        var result = Shell("$a = [string]::new('x', 300000000); Start-Sleep -Seconds 60").Run(Limits(seconds: 60, memory: 32L * 1024 * 1024));

        Assert.Equal(RunnerProcessStatus.MemoryLimitExceeded, result.Status);
    }
}
