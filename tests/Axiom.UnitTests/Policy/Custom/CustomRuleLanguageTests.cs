using System.Collections.Immutable;
using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Custom;
using static Axiom.UnitTests.Policy.PolicyTestKit;

namespace Axiom.UnitTests.Policy.Custom;

public class CustomRuleLanguageTests
{
    private static string Spec(string checks, string extra = "") =>
        $$"""{ "schema": "axiom-custom-rule/v1", {{extra}} "checks": [ {{checks}} ] }""";

    private static ImmutableArray<PolicyViolation> Run(
        string spec,
        ChangedFile[] changes,
        long budget = CustomRuleEvaluator.DefaultStepBudget,
        params (string, string)[] parameters) =>
        CustomRuleEvaluator.Evaluate(
            CustomRuleSpec.Parse(spec).Bind(Args(parameters)),
            changes,
            [.. InMemoryRepositoryView.FromChanges(changes).Paths],
            budget);

    [Theory]
    [InlineData("""{ "schema": "other/v9", "checks": [ { "type": "forbid-path" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "exec", "command": "rm -rf /" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "assembly": "evil.dll", "checks": [ { "type": "forbid-path" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "forbid-path", "extra": 1 } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "forbid-regex" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "json-path", "path": "$..x", "op": "exists" } ] }""")]
    [InlineData("""{ "schema": "axiom-custom-rule/v1", "select": { "kinds": ["teleported"] }, "checks": [ { "type": "forbid-path" } ] }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Malformed_or_unknown_definitions_are_rejected(string spec)
    {
        var ex = Assert.Throws<CustomRuleException>(() => CustomRuleSpec.Parse(spec));
        Assert.Equal(CustomRuleCodes.InvalidRule, ex.Code);
    }

    [Fact]
    public void An_oversized_definition_is_rejected()
    {
        var huge = Spec("{ \"type\": \"forbid-path\", \"message\": \"x\" }", $"\"description\": \"{new string('a', CustomRuleSpec.MaxSpecCharacters)}\",");
        Assert.Throws<CustomRuleException>(() => CustomRuleSpec.Parse(huge));
    }

    [Theory]
    [InlineData("(?<=a)b")]
    [InlineData("(a)\\1")]
    [InlineData("(unclosed")]
    public void Unsupported_or_invalid_regexes_are_errors_not_passes(string pattern)
    {
        var spec = Spec($$"""{ "type": "forbid-regex", "pattern": "{{pattern.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }""");

        var ex = Assert.Throws<CustomRuleException>(() => Run(spec, [Added("a.cs", "ab")]));
        Assert.Equal(CustomRuleCodes.InvalidRule, ex.Code);
    }

    [Fact]
    public void Forbid_regex_checks_added_lines_only_by_default()
    {
        var spec = Spec("""{ "type": "forbid-regex", "pattern": "TODO\\(\\w+\\)", "message": "No owner-less TODOs." }""");
        var change = Modified("src/A.cs", "// TODO(bob)\nint x;", "// TODO(old)\n", new AddedLine(2, "int x; // TODO(amy)"));

        var violation = Assert.Single(Run(spec, [change]));

        Assert.Equal("src/A.cs", violation.Path);
        Assert.Equal(2, violation.Line);
        Assert.Equal("No owner-less TODOs.", violation.Message);
        Assert.StartsWith("matches ", violation.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Forbid_regex_over_content_reports_line_numbers()
    {
        var spec = Spec("""{ "type": "forbid-regex", "scope": "content", "pattern": "password\\s*=" }""");

        var violations = Run(spec, [Added("a.cfg", "ok\npassword = x\nok\npassword=y")]);

        Assert.Equal([2, 4], violations.Select(v => v.Line!.Value));
    }

    [Fact]
    public void Selection_limits_the_files_and_kinds_examined()
    {
        var spec = Spec("""{ "type": "forbid-path" }""", "\"select\": { \"paths\": [\"src/**\"], \"exclude\": [\"src/gen/**\"], \"kinds\": [\"added\"] },");

        var violations = Run(spec, [Added("src/a.cs", "x"), Added("src/gen/b.cs", "x"), Added("docs/c.md", "x"), Modified("src/d.cs", "x", "y")]);

        Assert.Equal(["src/a.cs"], violations.Select(v => v.Path!));
    }

    [Fact]
    public void Require_regex_reports_each_file_lacking_the_pattern()
    {
        var spec = Spec("""{ "type": "require-regex", "pattern": "^// Copyright" }""", "\"select\": { \"paths\": [\"**/*.cs\"] },");

        var violations = Run(spec, [Added("a.cs", "// Copyright Acme\nx"), Added("b.cs", "x"), Deleted("c.cs")]);

        Assert.Equal(["b.cs"], violations.Select(v => v.Path!));
    }

    [Fact]
    public void Require_file_looks_at_the_whole_tree()
    {
        var spec = Spec("""{ "type": "require-file", "path": "**/OWNERS" }""");

        Assert.Empty(Run(spec, [Added("svc/OWNERS", "x")]));
        Assert.Single(Run(spec, [Added("svc/a.cs", "x")]));
    }

    [Theory]
    [InlineData("exists", "$.engines.node", null, 0)]
    [InlineData("exists", "$.engines.deno", null, 1)]
    [InlineData("absent", "$.private", null, 1)]
    [InlineData("equals", "$.license", "MIT", 0)]
    [InlineData("equals", "$.license", "GPL", 1)]
    [InlineData("not-equals", "$.license", "GPL", 0)]
    [InlineData("matches", "$.version", "^1\\\\.", 0)]
    [InlineData("matches", "$.version", "^2\\\\.", 1)]
    [InlineData("equals", "$.keywords[*]", "a", 1)]
    [InlineData("equals", "$.keywords[0]", "a", 0)]
    [InlineData("equals", "$['license']", "MIT", 0)]
    public void Json_path_assertions(string op, string path, string? value, int expectedViolations)
    {
        var valueJson = value is null ? string.Empty : $", \"value\": \"{value}\"";
        var spec = Spec($$"""{ "type": "json-path", "path": "{{path}}", "op": "{{op}}"{{valueJson}} }""");
        const string package = """{ "license": "MIT", "private": true, "version": "1.2.3", "engines": { "node": "20" }, "keywords": ["a", "b"] }""";

        Assert.Equal(expectedViolations, Run(spec, [Added("package.json", package)]).Length);
    }

    [Fact]
    public void Json_path_over_invalid_json_is_a_violation_not_a_pass()
    {
        var spec = Spec("""{ "type": "json-path", "path": "$.a", "op": "absent" }""");

        Assert.Single(Run(spec, [Added("x.json", "{ not json")]));
    }

    [Fact]
    public void Parameters_are_substituted_defaulted_and_validated()
    {
        var spec = Spec(
            """{ "type": "forbid-regex", "pattern": "{{word}}", "message": "Do not use {{word}}." }""",
            "\"parameters\": { \"word\": null, \"extra\": \"x\" },");

        var violation = Assert.Single(Run(spec, [Added("a.txt", "a banned word")], parameters: ("word", "banned")));
        Assert.Equal("Do not use banned.", violation.Message);

        Assert.Equal(CustomRuleCodes.InvalidParameters, Assert.Throws<CustomRuleException>(() => Run(spec, [Added("a.txt", "x")])).Code);
        Assert.Equal(
            CustomRuleCodes.InvalidParameters,
            Assert.Throws<CustomRuleException>(() => Run(spec, [Added("a.txt", "x")], parameters: [("word", "a"), ("nope", "b")])).Code);
    }

    [Fact]
    public void A_substituted_value_is_not_expanded_again()
    {
        var spec = Spec("""{ "type": "forbid-path", "message": "{{a}}" }""", "\"parameters\": { \"a\": null, \"b\": \"SECRET\" },");

        var violation = Assert.Single(Run(spec, [Added("a.txt", "x")], parameters: ("a", "{{b}}")));

        Assert.Equal("{{b}}", violation.Message);
    }

    [Fact]
    public void An_undeclared_placeholder_is_an_error()
    {
        var spec = Spec("""{ "type": "forbid-path", "message": "{{ghost}}" }""");

        Assert.Throws<CustomRuleException>(() => Run(spec, [Added("a.txt", "x")]));
    }

    [Fact]
    public void The_step_budget_stops_runaway_work()
    {
        var spec = Spec("""{ "type": "forbid-regex", "scope": "content", "pattern": "zzz" }""");

        var ex = Assert.Throws<CustomRuleException>(() => Run(spec, [Added("big.txt", new string('a', 10_000))], budget: 1_000));

        Assert.Equal(CustomRuleCodes.StepBudgetExceeded, ex.Code);
    }

    [Fact]
    public void Violations_are_capped_and_ordered_deterministically()
    {
        var spec = Spec("""{ "type": "forbid-path" }""");
        var files = Enumerable.Range(0, 800).Select(i => Added($"f{i:D4}.txt", "x")).ToArray();

        var forward = Run(spec, files);
        var backward = Run(spec, [.. files.Reverse()]);

        Assert.Equal(CustomRuleEvaluator.MaxViolations, forward.Length);
        Assert.Equal(forward.Select(v => v.Path), backward.Select(v => v.Path));
    }

    [Fact]
    public void Catastrophic_looking_patterns_finish_quickly_because_matching_is_linear()
    {
        var spec = Spec("""{ "type": "forbid-regex", "scope": "content", "pattern": "(a+)+$" }""");
        var content = new string('a', 50_000) + "!";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ = Run(spec, [Added("a.txt", content)]);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
