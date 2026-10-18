using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Axiom.Application.Candidates;

/// <summary>A decision-like statement found in a document, with where and how it was found.</summary>
public sealed record DocStatement(string Path, int Line, string Heading, string Sentence, string Method, double Confidence, string Phrase);

/// <summary>
/// Deterministic reader of repository documentation (README, <c>docs/**</c>, architecture notes) for statements that
/// look like decisions or rules. It matches fixed phrases sentence by sentence; there is no model and no scoring
/// beyond the table below, so the same text always yields the same statements. Confidence is deliberately low:
/// prose that says "must" is a lead for a human, not a decision.
/// </summary>
public static partial class DocsScanner
{
    public const string DecisionMethod = "docs-phrases/decision@1";
    public const string RuleMethod = "docs-phrases/rule@1";
    public const string DeprecationMethod = "docs-phrases/deprecation@1";

    public const int MaxFileBytes = 200_000;
    public const int MinSentenceLength = 20;
    public const int MaxSentenceLength = 400;

    private static readonly string[] IgnoredSegments = ["node_modules", "vendor", "third_party", "third-party", ".git", "bin", "obj", "dist", "build", "changelog"];
    private static readonly string[] RuleHeadings = ["decision", "rules", "rule", "convention", "conventions", "guidelines", "principles", "standards", "constraints", "requirements", "policy", "policies"];

    /// <summary>README files, markdown under <c>docs/</c> or <c>doc/</c>, and architecture notes. ADR directories belong to the ADR import.</summary>
    public static bool IsDocPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (!(normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)) || AdrParser.IsAdrPath(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/');
        if (segments[..^1].Any(s => IgnoredSegments.Contains(s.ToLowerInvariant())))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(segments[^1]).ToLowerInvariant();
        if (name.Contains("template", StringComparison.Ordinal) || IgnoredSegments.Contains(name))
        {
            return false;
        }

        return name.StartsWith("readme", StringComparison.Ordinal)
            || name.Contains("architecture", StringComparison.Ordinal)
            || segments[..^1].Any(s => s.ToLowerInvariant() is "docs" or "doc" or "documentation" or "architecture" or "design");
    }

    public static ImmutableArray<DocStatement> Scan(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿').Split('\n');
        var found = ImmutableArray.CreateBuilder<DocStatement>();
        var heading = string.Empty;
        var inFence = false;
        var paragraph = new List<string>();
        var paragraphStart = 0;

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                Extract(path, paragraphStart, heading, string.Join(' ', paragraph), found);
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var headingMatch = Heading().Match(trimmed);
            if (headingMatch.Success)
            {
                Flush();
                heading = CandidateText.PlainText(headingMatch.Groups[1].Value);
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('|') || trimmed.StartsWith("<!--", StringComparison.Ordinal) || trimmed.StartsWith("---", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            // Each list item is its own paragraph; wrapped continuation lines stay with the item.
            var bullet = Bullet().Match(trimmed);
            if (bullet.Success)
            {
                Flush();
                paragraphStart = i + 1;
                paragraph.Add(bullet.Groups[1].Value);
                continue;
            }

            if (paragraph.Count == 0)
            {
                paragraphStart = i + 1;
            }

            paragraph.Add(trimmed.TrimStart('>', ' '));
        }

        Flush();
        return found.ToImmutable();
    }

    internal static (string Method, double Confidence, string Phrase)? Classify(string sentence, string heading)
    {
        var inRuleSection = RuleHeadings.Any(h => heading.Contains(h, StringComparison.OrdinalIgnoreCase));
        var bonus = inRuleSection ? 0.1 : 0.0;

        if (DecidedPhrase().Match(sentence) is { Success: true } decided)
        {
            return (DecisionMethod, 0.6 + bonus, Normalize(decided.Value));
        }

        if (DeprecatedPhrase().Match(sentence) is { Success: true } deprecated)
        {
            return (DeprecationMethod, 0.4 + bonus, Normalize(deprecated.Value));
        }

        if (RulePhrase().Match(sentence) is { Success: true } rule)
        {
            var phrase = Normalize(rule.Value);
            return (RuleMethod, (phrase is "must not" or "never" ? 0.5 : 0.45) + bonus, phrase);
        }

        return null;
    }

    private static void Extract(string path, int line, string heading, string paragraph, ImmutableArray<DocStatement>.Builder found)
    {
        foreach (var raw in SentenceBreak().Split(CandidateText.PlainText(paragraph)))
        {
            var sentence = raw.Trim();
            if (sentence.Length < MinSentenceLength || sentence.Length > MaxSentenceLength || sentence.EndsWith('?') || IsExample(sentence))
            {
                continue;
            }

            if (Classify(sentence, heading) is { } hit)
            {
                found.Add(new DocStatement(path, line, heading, sentence, hit.Method, Math.Round(hit.Confidence, 2), hit.Phrase));
            }
        }
    }

    private static string Normalize(string phrase) => CandidateText.Collapse(phrase).ToLowerInvariant();

    private static bool IsExample(string sentence) =>
        sentence.StartsWith("e.g.", StringComparison.OrdinalIgnoreCase) || sentence.StartsWith("for example", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?(.+)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"(?<=[.!])\s+(?=[A-Z])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\b(?:we (?:have )?(?:decided|agreed|chose|chosen)|it was decided|the team (?:decided|agreed)|decision:)", RegexOptions.IgnoreCase)]
    private static partial Regex DecidedPhrase();

    [GeneratedRegex(@"\b(?:deprecated|is being phased out|no longer (?:supported|used))\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeprecatedPhrase();

    [GeneratedRegex(@"\b(?:must\s+not|must|never|always)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RulePhrase();
}
