using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Evaluation;

/// <summary>Change classes produced by the classifier; also usable as <c>scope.changeClasses</c> in records.</summary>
public static class ChangeClasses
{
    public const string NewComponent = "new-component";
    public const string CrossSystemDependency = "cross-system-dependency";
    public const string SecurityBoundary = "security-boundary";
    public const string DataModel = "data-model";
    public const string PublicContract = "public-contract";
    public const string Infrastructure = "infrastructure";
    public const string StrategicTechnology = "strategic-technology";
    public const string Migration = "migration";
    public const string MultiSystem = "multi-system";
    public const string OwnerPolicy = "owner-policy";
    public const string ArchitecturalDecision = "architectural-decision";
    public const string DocsOnly = "docs-only";
    public const string TestsOnly = "tests-only";
}

/// <summary>
/// One configured trigger of the significant-change policy (design.md §9). A trigger fires when any of
/// its path globs matches a changed path, any keyword occurs in the task text, or a threshold is met.
/// </summary>
public sealed record SignificanceTriggerDefinition(
    string Code,
    string ChangeClass,
    string Description,
    ImmutableArray<string> PathGlobs,
    ImmutableArray<string> Keywords,
    int? MinSystems = null,
    int? MinRepositories = null,
    string? SourceRecordId = null);

public sealed record SignificanceTrigger(string Code, string ChangeClass, string Explanation, ImmutableArray<string> Evidence, string? SourceRecordId);

public sealed record SignificanceAssessment(bool IsSignificant, ImmutableArray<SignificanceTrigger> Triggers, ImmutableArray<string> ChangeClasses);

public sealed record SignificanceInput(
    string? TaskText,
    ImmutableArray<string> Paths,
    int SystemCount,
    int RepositoryCount,
    bool CrossSystemDependency,
    ImmutableArray<SignificanceTriggerDefinition> AdditionalTriggers);

/// <summary>
/// Deterministic, explainable significant-change classifier. It never consults a model; a semantic
/// "architectural decision likely" signal is added by the evaluation orchestrator as a separate trigger.
/// </summary>
public static class SignificantChangeClassifier
{
    /// <summary>Rule ID used in governance records to contribute organization-defined triggers.</summary>
    public const string TriggerRuleId = "significant-change-trigger";

    private static readonly ImmutableArray<string> DocumentationGlobs = ["**/*.md", "**/*.mdx", "**/*.txt", "docs/**", "**/docs/**", "LICENSE", "**/LICENSE"];

    private static readonly ImmutableArray<string> TestGlobs =
        ["tests/**", "test/**", "**/tests/**", "**/test/**", "**/__tests__/**", "**/*.Tests/**", "**/*Tests.cs", "**/*.test.ts", "**/*.spec.ts", "**/*_test.go", "**/test_*.py"];

    /// <summary>Default triggers taken from the list in design.md §9.</summary>
    public static ImmutableArray<SignificanceTriggerDefinition> DefaultTriggers { get; } =
    [
        new("NEW_COMPONENT", ChangeClasses.NewComponent, "Introduces a new service, component, API, queue or database.",
            [],
            ["new service", "new microservice", "new component", "new api", "new endpoint group", "new queue", "new topic", "new database", "new datastore", "create a service", "add a service"]),
        new("SECURITY_BOUNDARY", ChangeClasses.SecurityBoundary, "Changes an authentication, authorization or security boundary.",
            ["**/auth/**", "**/Auth/**", "**/authentication/**", "**/Authentication/**", "**/authorization/**", "**/Authorization/**", "**/security/**", "**/Security/**", "**/*Authorization*", "**/*Authentication*"],
            ["authentication", "authorization", "oauth", "oidc", "rbac", "permission model", "access control", "encryption", "token validation", "security boundary"]),
        new("DATA_MODEL", ChangeClasses.DataModel, "Changes data ownership or the storage model.",
            ["**/migrations/**", "**/Migrations/**", "**/*.sql", "**/schema/**", "**/schemas/**"],
            ["database schema", "data model", "storage model", "data ownership", "new table", "drop table"]),
        new("PUBLIC_CONTRACT", ChangeClasses.PublicContract, "Changes a public contract.",
            ["**/openapi*.yaml", "**/openapi*.yml", "**/openapi*.json", "**/swagger*.json", "**/*.proto", "**/*.graphql", "**/asyncapi*.yaml", "**/asyncapi*.yml", "**/contracts/**"],
            ["public api", "public contract", "api contract", "breaking change", "event schema", "event contract", "remove endpoint", "remove the api"]),
        new("INFRASTRUCTURE", ChangeClasses.Infrastructure, "Changes infrastructure or deployment topology.",
            ["deploy/**", "**/helm/**", "**/kustomize/**", "**/k8s/**", "**/kubernetes/**", "**/*.tf", "**/Dockerfile", "Dockerfile", ".github/workflows/**", "**/azure-pipelines*.yml", "azure-pipelines*.yml"],
            ["deployment topology", "kubernetes", "terraform", "infrastructure change", "network policy", "ingress"]),
        new("STRATEGIC_TECHNOLOGY", ChangeClasses.StrategicTechnology, "Introduces or removes a strategic technology.",
            [],
            ["new framework", "new cache", "new message broker", "switch to", "replace with", "adopt", "introduce redis", "introduce kafka", "new orm", "new language"]),
        new("MIGRATION", ChangeClasses.Migration, "Performs a migration.",
            [],
            ["migration", "migrate"]),
        new("MULTI_SYSTEM", ChangeClasses.MultiSystem, "Touches several systems or repositories.",
            [], [], MinSystems: 2, MinRepositories: 2),
    ];

    public static SignificanceAssessment Classify(SignificanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var triggers = ImmutableArray.CreateBuilder<SignificanceTrigger>();
        var paths = input.Paths.Select(GlobPattern.NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var words = Tokenize(input.TaskText);

        foreach (var definition in DefaultTriggers.Concat(input.AdditionalTriggers).OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.SourceRecordId, StringComparer.Ordinal))
        {
            var evidence = ImmutableArray.CreateBuilder<string>();

            foreach (var path in paths)
            {
                var hit = definition.PathGlobs.FirstOrDefault(glob => Intersects(glob, path));
                if (hit is not null)
                {
                    evidence.Add($"path '{path}' matches '{hit}'");
                }
            }

            foreach (var keyword in definition.Keywords.Order(StringComparer.Ordinal))
            {
                if (ContainsPhrase(words, keyword))
                {
                    evidence.Add($"task mentions '{keyword}'");
                }
            }

            if (definition.MinSystems is { } minSystems && input.SystemCount >= minSystems)
            {
                evidence.Add($"change touches {input.SystemCount} systems (threshold {minSystems})");
            }

            if (definition.MinRepositories is { } minRepositories && input.RepositoryCount >= minRepositories)
            {
                evidence.Add($"change touches {input.RepositoryCount} repositories (threshold {minRepositories})");
            }

            if (evidence.Count > 0)
            {
                triggers.Add(new SignificanceTrigger(definition.Code, definition.ChangeClass, definition.Description, evidence.ToImmutable(), definition.SourceRecordId));
            }
        }

        if (input.CrossSystemDependency)
        {
            triggers.Add(new SignificanceTrigger("CROSS_SYSTEM_DEPENDENCY", ChangeClasses.CrossSystemDependency,
                "Adds or changes a dependency that crosses a system boundary.", ["topology shows a cross-system dependency"], null));
        }

        var classes = triggers.Select(t => t.ChangeClass).ToHashSet(StringComparer.Ordinal);
        if (paths.Length > 0 && paths.All(p => DocumentationGlobs.Any(g => Intersects(g, p) && !GlobPattern.Parse(p).HasWildcards)))
        {
            classes.Add(ChangeClasses.DocsOnly);
        }
        else if (paths.Length > 0 && paths.All(p => TestGlobs.Any(g => Intersects(g, p) && !GlobPattern.Parse(p).HasWildcards)))
        {
            classes.Add(ChangeClasses.TestsOnly);
        }

        return new SignificanceAssessment(triggers.Count > 0, triggers.ToImmutable(), [.. classes.Order(StringComparer.Ordinal)]);
    }

    /// <summary>Reads organization-defined triggers from the rule bindings of applicable governance records.</summary>
    public static ImmutableArray<SignificanceTriggerDefinition> TriggersFrom(IEnumerable<GovernanceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var definitions = ImmutableArray.CreateBuilder<SignificanceTriggerDefinition>();
        foreach (var record in records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            foreach (var binding in record.Enforcement.Rules.Where(r => r.RuleId == TriggerRuleId))
            {
                definitions.Add(new SignificanceTriggerDefinition(
                    binding.Parameters.GetValueOrDefault("code", "OWNER_POLICY"),
                    binding.Parameters.GetValueOrDefault("changeClass", ChangeClasses.OwnerPolicy),
                    binding.Parameters.GetValueOrDefault("description", $"Decision-owner policy {record.Id} marks this change as significant."),
                    SplitList(binding.Parameters.GetValueOrDefault("paths")),
                    SplitList(binding.Parameters.GetValueOrDefault("keywords")),
                    ParseThreshold(binding.Parameters.GetValueOrDefault("minSystems")),
                    ParseThreshold(binding.Parameters.GetValueOrDefault("minRepositories")),
                    record.Id));
            }
        }

        return definitions.ToImmutable();
    }

    private static bool Intersects(string glob, string path)
    {
        var pattern = GlobPattern.Parse(glob);
        var candidate = GlobPattern.Parse(path);
        return candidate.HasWildcards ? pattern.MayIntersect(candidate) : pattern.IsMatch(path);
    }

    private static ImmutableArray<string> SplitList(string? value) =>
        value is null ? [] : [.. value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static int? ParseThreshold(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    private static string[] Tokenize(string? text) =>
        text is null
            ? []
            : new string([.. text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')]).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whole-word, case-insensitive phrase match so that "migrate" does not fire on "immigrated".</summary>
    private static bool ContainsPhrase(string[] words, string phrase)
    {
        var needle = Tokenize(phrase);
        if (needle.Length == 0 || needle.Length > words.Length)
        {
            return false;
        }

        for (var start = 0; start <= words.Length - needle.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length && matched; offset++)
            {
                matched = string.Equals(words[start + offset], needle[offset], StringComparison.Ordinal);
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
