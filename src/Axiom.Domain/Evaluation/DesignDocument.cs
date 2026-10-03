using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Axiom.Domain.Evaluation;

/// <summary>
/// A significant-change design (design.md §8.2, governance/schemas/design.schema.json). Sections are
/// kept as text; a section is "present" when it has non-whitespace content.
/// </summary>
public sealed partial record DesignDocument
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Goal { get; init; } = string.Empty;

    public ImmutableArray<string> NonGoals { get; init; } = [];

    public ImmutableArray<string> AffectedSystems { get; init; } = [];

    public ImmutableArray<string> AffectedRepositories { get; init; } = [];

    public string CurrentBehavior { get; init; } = string.Empty;

    public string ProposedBehavior { get; init; } = string.Empty;

    public string DataFlow { get; init; } = string.Empty;

    public ImmutableArray<string> DecisionsConsulted { get; init; } = [];

    public ImmutableArray<string> NewDecisionCandidates { get; init; } = [];

    public string Contracts { get; init; } = string.Empty;

    public string FailureModes { get; init; } = string.Empty;

    public string Security { get; init; } = string.Empty;

    public string Observability { get; init; } = string.Empty;

    public string Migration { get; init; } = string.Empty;

    public string Rollout { get; init; } = string.Empty;

    /// <summary>Whether <c>nonGoals</c> / <c>decisionsConsulted</c> were stated at all (an explicit empty list is a statement).</summary>
    public bool NonGoalsStated { get; init; }

    public bool DecisionsConsultedStated { get; init; }

    /// <summary>All substantive text of the design, used for deterministic phrase checks.</summary>
    public string SubstantiveText => string.Join("\n", Goal, ProposedBehavior, DataFlow, Contracts, Security, Migration, Rollout);

    /// <summary>
    /// Hash over the normalized material content. Whitespace and formatting changes keep the hash;
    /// any substantive change produces a new one, which is what invalidates an earlier approval.
    /// </summary>
    public string MaterialHash
    {
        get
        {
            var builder = new StringBuilder("axiom-design-v1");
            void Add(string name, string value) => builder.Append('\n').Append(name).Append('=').Append(Normalize(value));
            void AddList(string name, ImmutableArray<string> values) =>
                Add(name, string.Join("|", values.Select(Normalize).Where(v => v.Length > 0).Order(StringComparer.Ordinal)));

            Add("id", Id);
            Add("goal", Goal);
            AddList("nonGoals", NonGoals);
            AddList("affectedSystems", AffectedSystems);
            AddList("affectedRepositories", AffectedRepositories);
            Add("currentBehavior", CurrentBehavior);
            Add("proposedBehavior", ProposedBehavior);
            Add("dataFlow", DataFlow);
            AddList("decisionsConsulted", DecisionsConsulted);
            AddList("newDecisionCandidates", NewDecisionCandidates);
            Add("contracts", Contracts);
            Add("failureModes", FailureModes);
            Add("security", Security);
            Add("observability", Observability);
            Add("migration", Migration);
            Add("rollout", Rollout);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }

    private static string Normalize(string value) => WhitespaceRegex().Replace(value, " ").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRegex();
}

public sealed record DesignParseResult(DesignDocument? Design, ImmutableArray<string> Errors);

/// <summary>Parses designs from Markdown (heading per section) or structured JSON (task 6.3).</summary>
public static partial class DesignParser
{
    private static readonly ImmutableDictionary<string, string> Headings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["goal"] = "goal",
        ["goals"] = "goal",
        ["non goals"] = "nonGoals",
        ["nongoals"] = "nonGoals",
        ["affected systems"] = "affectedSystems",
        ["affected components repositories"] = "affectedRepositories",
        ["affected repositories"] = "affectedRepositories",
        ["affected components"] = "affectedRepositories",
        ["decisions consulted"] = "decisionsConsulted",
        ["current behavior"] = "currentBehavior",
        ["current behaviour"] = "currentBehavior",
        ["proposed behavior"] = "proposedBehavior",
        ["proposed behaviour"] = "proposedBehavior",
        ["data control flow"] = "dataFlow",
        ["data flow"] = "dataFlow",
        ["control flow"] = "dataFlow",
        ["contracts"] = "contracts",
        ["failure modes"] = "failureModes",
        ["security"] = "security",
        ["security privacy"] = "security",
        ["observability"] = "observability",
        ["migration"] = "migration",
        ["migration rollout"] = "rollout",
        ["rollout"] = "rollout",
        ["new decision candidates"] = "newDecisionCandidates",
        ["new decisions"] = "newDecisionCandidates",
    }.ToImmutableDictionary();

    public static DesignParseResult ParseMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string id = string.Empty, title = string.Empty;
        StringBuilder? current = null;
        var inFence = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && line.StartsWith("# ", StringComparison.Ordinal) && title.Length == 0)
            {
                var heading = line[2..].Trim();
                var match = GovernanceIdRegex().Match(heading);
                id = match.Success && match.Index == 0 ? match.Value : string.Empty;
                title = id.Length > 0 ? heading[id.Length..].TrimStart(' ', '—', '-', ':', '–').Trim() : heading;
                continue;
            }

            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                var key = new string([.. line[3..].ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ')]);
                key = string.Join(' ', key.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                current = Headings.TryGetValue(key, out var field)
                    ? sections.TryGetValue(field, out var existing) ? existing : sections[field] = new StringBuilder()
                    : null;
                continue;
            }

            current?.AppendLine(line);
        }

        if (title.Length == 0)
        {
            return new DesignParseResult(null, ["The design has no title heading ('# DESIGN-123 — Title')."]);
        }

        string Text(string field) => sections.TryGetValue(field, out var text) ? text.ToString().Trim() : string.Empty;
        ImmutableArray<string> Items(string field) =>
        [
            .. Text(field).Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal))
                .Select(l => l[2..].Trim())
                .Where(l => l.Length > 0),
        ];

        var affected = Items("affectedRepositories");
        return new DesignParseResult(
            new DesignDocument
            {
                Id = id,
                Title = title,
                Goal = Text("goal"),
                NonGoals = Items("nonGoals"),
                NonGoalsStated = sections.ContainsKey("nonGoals"),
                AffectedSystems = Items("affectedSystems"),
                // Lines may read "component / repository"; the repository is the last part.
                AffectedRepositories = [.. affected.Select(a => a.Split('/')[^1].Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
                CurrentBehavior = Text("currentBehavior"),
                ProposedBehavior = Text("proposedBehavior"),
                DataFlow = Text("dataFlow"),
                DecisionsConsulted = [.. GovernanceIdRegex().Matches(Text("decisionsConsulted")).Select(m => m.Value).Distinct(StringComparer.Ordinal)],
                DecisionsConsultedStated = sections.ContainsKey("decisionsConsulted"),
                NewDecisionCandidates = Items("newDecisionCandidates"),
                Contracts = Text("contracts"),
                FailureModes = Text("failureModes"),
                Security = Text("security"),
                Observability = Text("observability"),
                Migration = Text("migration"),
                Rollout = Text("rollout"),
            },
            []);
    }

    public static DesignParseResult ParseJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return new DesignParseResult(null, ["The design must be a JSON object."]);
        }

        var errors = ImmutableArray.CreateBuilder<string>();

        string Text(string name)
        {
            if (!json.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return string.Empty;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!.Trim(),
                JsonValueKind.Object when !value.EnumerateObject().Any() => string.Empty,
                JsonValueKind.Array when value.GetArrayLength() == 0 => string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => value.ToString(),
            };
        }

        ImmutableArray<string> Items(string name)
        {
            if (!json.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"'{name}' must be an array.");
                return [];
            }

            return [.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : v.GetRawText()).Where(v => v.Length > 0)];
        }

        var design = new DesignDocument
        {
            Id = Text("id"),
            Title = Text("title"),
            Goal = Text("goal"),
            NonGoals = Items("nonGoals"),
            NonGoalsStated = json.TryGetProperty("nonGoals", out var nonGoals) && nonGoals.ValueKind == JsonValueKind.Array,
            AffectedSystems = Items("affectedSystems"),
            AffectedRepositories = Items("affectedRepositories"),
            CurrentBehavior = Text("currentBehavior"),
            ProposedBehavior = Text("proposedBehavior"),
            DataFlow = Text("dataFlow"),
            DecisionsConsulted = Items("decisionsConsulted"),
            DecisionsConsultedStated = json.TryGetProperty("decisionsConsulted", out var consulted) && consulted.ValueKind == JsonValueKind.Array,
            NewDecisionCandidates = Items("newDecisionCandidates"),
            Contracts = Text("contracts"),
            FailureModes = Text("failureModes"),
            Security = Text("security"),
            Observability = Text("observability"),
            Migration = Text("migration"),
            Rollout = Text("rollout"),
        };

        return errors.Count > 0 ? new DesignParseResult(null, errors.ToImmutable()) : new DesignParseResult(design, []);
    }

    [GeneratedRegex(@"\b[A-Z][A-Z0-9_]*(?:-[A-Z][A-Z0-9_]*)*-[0-9]{3,}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex GovernanceIdRegex();
}
