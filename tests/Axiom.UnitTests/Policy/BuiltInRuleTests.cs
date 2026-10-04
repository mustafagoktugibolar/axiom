using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Rules;
using static Axiom.UnitTests.Policy.PolicyTestKit;

namespace Axiom.UnitTests.Policy;

public class BuiltInRuleTests
{
    [Fact]
    public void The_catalog_is_ordered_unique_and_every_rule_documents_its_parameters()
    {
        var ids = BuiltInRules.All.Select(r => r.RuleId).ToArray();

        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(BuiltInRules.All, rule =>
        {
            Assert.Matches("^[a-z]+(-[a-z]+)*$", rule.RuleId);
            Assert.Matches(@"^\d+\.\d+\.\d+$", rule.Version);
            Assert.NotEmpty(rule.Description);
            Assert.NotEmpty(rule.Parameters);
        });
    }

    [Fact]
    public void Every_rule_rejects_unknown_parameters()
    {
        foreach (var rule in BuiltInRules.All)
        {
            Assert.Throws<PolicyParameterException>(() => rule.Evaluate(Context(), Args(("no-such-parameter", "x"))));
        }
    }

    [Fact]
    public void Forbidden_path_change_checks_both_ends_of_a_rename()
    {
        var violations = Run(new ForbiddenPathChangeRule(), Context(Renamed("src/Generated/A.cs", "src/B.cs")), ("paths", "src/Generated/**"));

        var violation = Assert.Single(violations);
        Assert.Equal("src/Generated/A.cs", violation.Path);
    }

    [Fact]
    public void Forbidden_path_change_honours_kinds_and_exclusions()
    {
        var context = Context(Added("vendor/a.js", "x"), Deleted("vendor/b.js"), Added("vendor/keep/c.js", "x"));

        var violations = Run(new ForbiddenPathChangeRule(), context, ("paths", "vendor/**"), ("kinds", "added"), ("exclude", "vendor/keep/**"));

        Assert.Equal(["vendor/a.js"], violations.Select(v => v.Path));
    }

    [Fact]
    public void Forbidden_path_change_requires_paths()
    {
        Assert.Throws<PolicyParameterException>(() => Run(new ForbiddenPathChangeRule(), Context(Added("a", "b"))));
    }

    [Fact]
    public void Required_tests_demands_a_test_change_next_to_source_changes()
    {
        var rule = new RequiredTestsRule();
        var args = new[] { ("source", "src/**/*.cs"), ("tests", "tests/**/*.cs") };

        Assert.Single(Run(rule, Context(Added("src/Billing.cs", "class A;")), args));
        Assert.Empty(Run(rule, Context(Added("src/Billing.cs", "class A;"), Added("tests/BillingTests.cs", "class T;")), args));
        Assert.Empty(Run(rule, Context(Added("docs/readme.md", "hi")), args));
    }

    [Fact]
    public void Required_tests_by_name_pairs_each_source_file()
    {
        var context = Context(Added("src/Billing.cs", "a"), Added("src/Orders.cs", "b"), Added("tests/BillingTests.cs", "t"));

        var violations = Run(new RequiredTestsRule(), context, ("source", "src/**"), ("tests", "tests/**"), ("pairing", "name"));

        Assert.Equal(["src/Orders.cs"], violations.Select(v => v.Path));
    }

    [Fact]
    public void Required_tests_ignores_deleted_tests()
    {
        var context = Context(Added("src/Billing.cs", "a"), Deleted("tests/BillingTests.cs"));

        Assert.Single(Run(new RequiredTestsRule(), context, ("source", "src/**"), ("tests", "tests/**")));
    }

    [Fact]
    public void No_secrets_flags_a_token_without_repeating_it()
    {
        const string token = "ghp_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8";
        var context = Context(Added("src/Client.cs", $"var header = \"{token}\";"));

        var violation = Assert.Single(Run(new NoSecretsRule(), context));

        Assert.Equal("src/Client.cs", violation.Path);
        Assert.Equal(1, violation.Line);
        Assert.DoesNotContain(token, violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, violation.Evidence ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void No_secrets_flags_private_keys_and_ignores_placeholders()
    {
        var key = Added("deploy/key.pem", "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8Qu\n-----END RSA PRIVATE KEY-----");
        var placeholders = Added("config/app.yaml", "password: ${DB_PASSWORD}\napi_key: <your-key>\nsecret: changeme");

        Assert.NotEmpty(Run(new NoSecretsRule(), Context(key)));
        Assert.Empty(Run(new NoSecretsRule(), Context(placeholders)));
    }

    [Fact]
    public void No_secrets_only_scans_added_lines_of_modified_files()
    {
        const string token = "ghp_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8";
        var existing = Modified("src/Old.cs", $"// {token}\nvar x = 1;", $"// {token}", new AddedLine(2, "var x = 1;"));

        Assert.Empty(Run(new NoSecretsRule(), Context(existing)));
    }

    [Fact]
    public void Forbidden_package_matches_versions_in_a_manifest()
    {
        const string manifest = """
            <Project>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="12.0.1" />
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """;
        var context = Context(Added("src/App/App.csproj", manifest));

        var violation = Assert.Single(Run(new ForbiddenPackageRule(), context, ("packages", "Newtonsoft.Json@<13.0.1, Serilog@<3.0.0")));

        Assert.Contains("Newtonsoft.Json", violation.Message, StringComparison.Ordinal);
        Assert.Empty(Run(new ForbiddenPackageRule(), context, ("packages", "Newtonsoft.Json@>=13.0.1")));
    }

    [Fact]
    public void Forbidden_package_reports_an_unparseable_manifest_instead_of_passing_it()
    {
        var context = Context(Added("package.json", "{ not json"));

        Assert.NotEmpty(Run(new ForbiddenPackageRule(), context, ("packages", "lodash")));
    }

    [Fact]
    public void Required_file_accepts_any_alternative_and_checks_content()
    {
        var rule = new RequiredFileRule();
        var tree = new PolicyContext("gateway", [], InMemoryRepositoryView.Of((".github/CODEOWNERS", "* @team-a")), GatewayChange);

        Assert.Empty(Run(rule, tree, ("files", "CODEOWNERS|.github/CODEOWNERS")));
        Assert.Empty(Run(rule, tree, ("files", "CODEOWNERS|.github/CODEOWNERS"), ("contains", "@team-a")));
        Assert.Single(Run(rule, tree, ("files", "CODEOWNERS|.github/CODEOWNERS"), ("contains", "@team-b")));

        var missing = Assert.Single(Run(rule, tree, ("files", "SECURITY.md")));
        Assert.Equal("SECURITY.md", missing.Path);
    }

    [Fact]
    public void Rules_are_deterministic_for_reordered_input()
    {
        var a = Added("src/A.cs", "x");
        var b = Added("src/B.cs", "y");

        var first = Run(new ForbiddenPathChangeRule(), Context(a, b), ("paths", "src/**"));
        var second = Run(new ForbiddenPathChangeRule(), Context(b, a), ("paths", "src/**"));

        Assert.Equal(first.Select(v => v.Path).Order(StringComparer.Ordinal), second.Select(v => v.Path).Order(StringComparer.Ordinal));
    }
}
