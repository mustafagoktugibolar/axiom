using System.Collections.Immutable;

namespace Axiom.Domain.Policy.Rules.Support;

/// <summary>Shared evaluation of forbidden references for the dependency-direction and forbidden-import rules.</summary>
internal static class ImportAnalysis
{
    public const string Languages =
        "C# (using directives, fully-qualified names), TypeScript/JavaScript (import, export from, require, dynamic import), "
        + "Python (import, from), Java/Kotlin (import, fully-qualified names), Go (import) and MSBuild project files (ProjectReference)";

    public static ImmutableArray<ModulePattern> Patterns(IEnumerable<string> values) => [.. values.Select(ModulePattern.Parse)];

    /// <summary>
    /// Forbidden references a file introduces. The whole file is evaluated when it is added or renamed;
    /// only its added lines when it is modified.
    /// </summary>
    public static IEnumerable<PolicyViolation> Find(
        ChangedFile file,
        ImmutableArray<ModulePattern> forbidden,
        ImmutableArray<ModulePattern> allowed,
        string because)
    {
        var language = SourceScanner.Detect(file.Path);
        if (language == SourceLanguage.None)
        {
            yield break;
        }

        IReadOnlyList<SourceReference> references;
        if (file.Content is not null)
        {
            var introduced = ChangeSet.IntroducedLines(file);
            var lines = ChangeSet.SplitLines(file.Content).Select((text, index) => new AddedLine(index + 1, text));
            references = [.. SourceScanner.References(language, lines, isFragment: false).Where(r => introduced(r.Line))];
        }
        else if (!file.AddedLines.IsDefaultOrEmpty)
        {
            references = SourceScanner.References(language, file.AddedLines.OrderBy(l => l.Number), isFragment: true);
        }
        else
        {
            yield break;
        }

        var reported = new HashSet<(int Line, string Pattern)>();
        foreach (var reference in references)
        {
            var pattern = forbidden.FirstOrDefault(p => p.IsMatch(reference.Target));
            if (pattern is null || allowed.Any(a => a.IsMatch(reference.Target)) || !reported.Add((reference.Line, pattern.Value)))
            {
                continue;
            }

            yield return new PolicyViolation(
                $"'{file.Path}' references '{reference.Target}', {because} ({pattern.Value}).",
                file.Path,
                reference.Line,
                $"{reference.Form}: {reference.Target}");
        }
    }
}
