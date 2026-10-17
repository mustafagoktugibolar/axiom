using System.Text.RegularExpressions;

namespace Axiom.UnitTests.Cli;

/// <summary>
/// Mechanical conformance of the harness starter packs under integrations/ (plan item 9.10):
/// real MCP tools, real axiom-cli commands and flags, no inline secrets, mandatory workflow statements.
/// </summary>
public sealed partial class IntegrationPackConformanceTests
{
    private static readonly string Root = FindRoot();
    private static readonly string[] Packs = ["agents-md", "kiro", "claude-code", "codex", "cursor", "copilot"];

    public static TheoryData<string> PackNames => new(Packs);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Axiom.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Axiom.slnx) not found.");
    }

    private static IEnumerable<string> PackFiles(string pack) =>
        Directory.EnumerateFiles(Path.Combine(Root, "integrations", pack), "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("README.md", StringComparison.Ordinal));

    private static string Read(string path) => File.ReadAllText(path);

    private static HashSet<string> RealTools() =>
        ToolNameRegex().Matches(Read(Path.Combine(Root, "src", "Axiom.Mcp", "GovernanceTools.cs")))
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static string CliSource() => Read(Path.Combine(Root, "tools", "axiom-cli", "AxiomCli.cs"));

    /// <summary>Pack text with CLAUDE.md-style "@file" imports of AGENTS.md resolved.</summary>
    private static string PackText(string pack)
    {
        var texts = PackFiles(pack).Select(Read).ToList();
        if (texts.Any(t => t.Contains("@AGENTS.md", StringComparison.Ordinal)))
            texts.Add(Read(Path.Combine(Root, "integrations", "agents-md", "AGENTS.md")));
        return string.Join('\n', texts);
    }

    [Fact]
    public void The_real_tool_list_is_found() => Assert.Contains("governance.preflight_change", RealTools());

    [Theory]
    [MemberData(nameof(PackNames))]
    public void A_pack_references_only_mcp_tools_that_exist(string pack)
    {
        var real = RealTools();
        foreach (var file in PackFiles(pack))
            foreach (Match m in ToolRefRegex().Matches(Read(file)))
                Assert.True(real.Contains(m.Value), $"{pack}: {Path.GetFileName(file)} references unknown MCP tool '{m.Value}'.");
    }

    [Theory]
    [MemberData(nameof(PackNames))]
    public void A_pack_references_only_cli_commands_and_flags_that_exist(string pack)
    {
        var source = CliSource();
        var commands = CommandRegex().Matches(source).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var flags = OptionRegex().Matches(source).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        flags.Add("--help");
        Assert.Contains("evaluate-diff", commands);

        foreach (var file in PackFiles(pack))
            foreach (var line in Read(file).Split('\n').Where(l => l.Contains("axiom-cli", StringComparison.Ordinal)))
            {
                foreach (Match m in CliInvocationRegex().Matches(line))
                    foreach (var name in m.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries))
                        Assert.True(commands.Contains(name), $"{pack}: unknown axiom-cli command '{name}' in {Path.GetFileName(file)}.");
                foreach (Match m in FlagRegex().Matches(line))
                    Assert.True(flags.Contains(m.Value), $"{pack}: unknown axiom-cli flag '{m.Value}' in {Path.GetFileName(file)}.");
            }
    }

    [Theory]
    [MemberData(nameof(PackNames))]
    public void A_pack_contains_no_inline_secrets(string pack)
    {
        foreach (var file in PackFiles(pack))
        {
            var text = Read(file);
            var name = Path.GetFileName(file);
            foreach (Match m in BearerRegex().Matches(text))
                Assert.True(EnvTokenRegex().IsMatch(m.Groups[1].Value), $"{pack}: {name} has a non-environment Bearer value '{m.Groups[1].Value}'.");
            Assert.False(SecretShapeRegex().IsMatch(text), $"{pack}: {name} contains something shaped like a credential.");
            Assert.False(InlineTokenRegex().IsMatch(text), $"{pack}: {name} assigns a literal token.");
        }
    }

    [Theory]
    [MemberData(nameof(PackNames))]
    public void A_pack_carries_the_mandatory_workflow(string pack)
    {
        var text = PackText(pack);
        foreach (var required in new[] { "governance.preflight_change", "governance.validate_design", "governance.validate_diff", "REQUIRE_REVIEW", "BLOCK" })
            Assert.True(text.Contains(required, StringComparison.Ordinal), $"{pack}: missing '{required}'.");
        Assert.True(ErrorNeverAllowRegex().IsMatch(text), $"{pack}: must say a tool error is never ALLOW.");
        Assert.DoesNotContain("DENY", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex", ".codex/config.toml.example", "bearer_token_env_var")]
    [InlineData("cursor", ".cursor/mcp.json.example", "${env:AXIOM_TOKEN}")]
    [InlineData("copilot", ".vscode/mcp.json.example", "${env:AXIOM_TOKEN}")]
    public void The_mcp_config_points_at_the_axiom_endpoint_with_an_environment_token(string pack, string relative, string marker)
    {
        var text = Read(Path.Combine(Root, "integrations", pack, relative));
        Assert.Matches("\"?url\"?\\s*[=:]\\s*\"https://[^\"]+/mcp\"", text);
        Assert.Contains(marker, text, StringComparison.Ordinal);
        Assert.Contains("AXIOM_TOKEN", text, StringComparison.Ordinal);
    }

    [GeneratedRegex("""McpServerTool\(Name = "(governance\.[a-z_]+)"?""")]
    private static partial Regex ToolNameRegex();

    [GeneratedRegex(@"governance\.[a-z_]+")]
    private static partial Regex ToolRefRegex();

    [GeneratedRegex("""new Command\("([a-z-]+)"?""")]
    private static partial Regex CommandRegex();

    [GeneratedRegex("""new Option<[^>]+>\("(--[a-z-]+)"?""")]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"axiom-cli ((?:[a-z][a-z-]*)(?: \| [a-z][a-z-]*)*)")]
    private static partial Regex CliInvocationRegex();

    [GeneratedRegex(@"(?<=\s)--[a-z][a-z-]*")]
    private static partial Regex FlagRegex();

    [GeneratedRegex(@"Bearer\s+(\S+)")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"^\$\{(env:)?AXIOM_TOKEN\}")]
    private static partial Regex EnvTokenRegex();

    [GeneratedRegex(@"(ghp_|gho_|github_pat_|sk-[A-Za-z0-9]|xox[abp]-|AKIA[0-9A-Z]{12}|eyJ[A-Za-z0-9_-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY)")]
    private static partial Regex SecretShapeRegex();

    [GeneratedRegex("""(?i)(token|secret|password|api[_-]?key)["']?\s*[=:]\s*["'][A-Za-z0-9+/_\-]{12,}["']""")]
    private static partial Regex InlineTokenRegex();

    [GeneratedRegex(@"(?is)(error|timeout)[^.]{0,100}never[^.]{0,40}(ALLOW|allowed)")]
    private static partial Regex ErrorNeverAllowRegex();
}
