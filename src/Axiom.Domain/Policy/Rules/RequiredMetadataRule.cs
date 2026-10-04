using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Required metadata: ownership, system and similar annotations must be present.</summary>
public sealed class RequiredMetadataRule : BuiltInRule
{
    public override string RuleId => "required-metadata";

    public override string Version => "1.0.0";

    public override string Description =>
        "Every file matching 'paths' must carry a non-empty value for each of 'keys'. Reads YAML (every document in the file), "
        + "JSON, and Markdown front matter. A file that cannot be read or parsed is reported.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("paths", "Required. Globs of the files that must carry the metadata, e.g. **/catalog-info.yaml or docs/**/*.md."),
        ("keys", "Required. Dotted keys, e.g. owner or metadata.annotations.backstage.io/owner. Keys containing dots are matched as written."),
        ("scope", "Optional. 'changed' (default) checks the added, modified and renamed files of the change; "
            + "'repository' checks every matching file in the repository tree."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var paths = parameters.RequiredGlobs("paths");
        var keys = parameters.RequiredList("keys");
        var wholeRepository = parameters.Choice("scope", "changed", "changed", "repository") == "repository";

        var files = wholeRepository
            ? context.Tree.Paths.Where(p => paths.MatchesAny(p)).Order(StringComparer.Ordinal).Select(p => (Path: p, Content: context.Tree.Read(p)))
            : ChangeSet.Present(context, paths, []).Select(c => (c.Path, c.Content));

        foreach (var (path, content) in files)
        {
            if (content is null || PolicyLimits.IsOversized(content))
            {
                yield return new PolicyViolation(
                    $"'{path}' could not be read (binary, unavailable or over {PolicyLimits.MaxFileCharacters} characters), so its metadata cannot be verified.",
                    path);
                continue;
            }

            IReadOnlyList<DocNode> documents;
            string? problem = null;
            try
            {
                documents = Read(path, content);
            }
            catch (FormatException ex)
            {
                documents = [];
                problem = ex.Message;
            }

            if (problem is not null)
            {
                yield return new PolicyViolation($"'{path}' could not be parsed, so its metadata cannot be verified.", path, Evidence: problem);
                continue;
            }

            if (documents.Count == 0)
            {
                yield return new PolicyViolation(
                    $"'{path}' has no metadata; required: {string.Join(", ", keys)}.", path, Evidence: $"missing keys: {string.Join(", ", keys)}");
                continue;
            }

            foreach (var document in documents)
            {
                foreach (var key in keys.Where(k => !StructuredDocument.HasValue(StructuredDocument.Resolve(document, k))))
                {
                    yield return new PolicyViolation($"'{path}' is missing required metadata '{key}'.", path, document.Line, $"missing key: {key}");
                }
            }
        }
    }

    private static IReadOnlyList<DocNode> Read(string path, string content)
    {
        if (StructuredDocument.IsJsonPath(path) || StructuredDocument.IsYamlPath(path))
        {
            return StructuredDocument.Parse(path, content);
        }

        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Only YAML, JSON and Markdown front matter are supported.");
        }

        var lines = ChangeSet.SplitLines(content);
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            return [];
        }

        var end = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
        if (end < 0)
        {
            throw new FormatException("The Markdown front matter is not terminated.");
        }

        // Keep the line count so reported lines match the file.
        return YamlReader.ParseDocuments("\n" + string.Join('\n', lines[1..end]));
    }
}
