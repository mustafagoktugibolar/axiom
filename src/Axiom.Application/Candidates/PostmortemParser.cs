using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Axiom.Application.Candidates;

public enum PostmortemSection
{
    RootCause,
    ActionItems,
    Lessons,
}

/// <param name="Statement">The invariant as a normative sentence ("X must ...").</param>
/// <param name="Original">The sentence as the postmortem wrote it.</param>
public sealed record PostmortemStatement(PostmortemSection Section, string SectionHeading, int Line, string Original, string Statement, double Confidence, bool Rewritten);

public sealed record PostmortemDocument(string Title, ImmutableArray<PostmortemStatement> Statements);

/// <summary>
/// Deterministic reader of markdown postmortems. It looks only at the "Root cause", "Action items" and "Lessons"
/// sections (and their usual synonyms) and keeps sentences that state a rule: those already saying must, never or
/// always; those saying should or needs to, rewritten to must; and, in action items, imperative bullets such as
/// "Add a timeout to every outbound call", rewritten to "Teams must add a timeout to every outbound call". The
/// original sentence travels with every result so a reviewer can see what was changed.
/// </summary>
public static partial class PostmortemParser
{
    public const string Method = "postmortem-markdown/section@1";
    public const int MinSentenceLength = 15;
    public const int MaxSentenceLength = 400;

    private static readonly string[] IgnoredNames = ["readme", "index", "template", "toc"];
    private static readonly string[] DirectoryNames = ["postmortem", "postmortems", "post-mortem", "post-mortems", "incident", "incidents", "incident-reports", "incident-review", "incident-reviews", "rca", "rcas"];
    private static readonly string[] ImperativeVerbs =
    [
        "add", "ensure", "make", "require", "enforce", "validate", "verify", "use", "set", "configure", "enable", "disable", "remove", "stop", "avoid",
        "document", "automate", "limit", "monitor", "alert", "review", "test", "check", "introduce", "implement", "replace", "migrate", "pin", "rotate", "restrict",
    ];

    public static bool IsPostmortemPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = normalized.Split('/');
        var name = Path.GetFileNameWithoutExtension(segments[^1]).ToLowerInvariant();
        if (IgnoredNames.Any(i => name == i || name.Contains("template", StringComparison.Ordinal)))
        {
            return false;
        }

        return segments[..^1].Any(s => DirectoryNames.Contains(s.ToLowerInvariant()))
            || name.Contains("postmortem", StringComparison.Ordinal) || name.Contains("post-mortem", StringComparison.Ordinal) || name.Contains("incident", StringComparison.Ordinal);
    }

    public static PostmortemDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿').Split('\n');
        var title = string.Empty;
        PostmortemSection? current = null;
        var currentHeading = string.Empty;
        var inFence = false;
        var statements = ImmutableArray.CreateBuilder<PostmortemStatement>();
        var paragraph = new List<string>();
        var start = 0;

        void Flush()
        {
            if (paragraph.Count > 0 && current is { } section)
            {
                Extract(section, currentHeading, start, string.Join(' ', paragraph), statements);
            }

            paragraph.Clear();
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

            var heading = Heading().Match(trimmed);
            if (heading.Success)
            {
                Flush();
                var text = CandidateText.PlainText(heading.Groups[2].Value);
                if (title.Length == 0 && heading.Groups[1].Value.Length == 1)
                {
                    title = text;
                }

                current = SectionOf(text);
                currentHeading = text;
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith('|'))
            {
                Flush();
                continue;
            }

            var bullet = Bullet().Match(trimmed);
            if (bullet.Success)
            {
                Flush();
                start = i + 1;
                paragraph.Add(bullet.Groups[1].Value);
                continue;
            }

            if (paragraph.Count == 0)
            {
                start = i + 1;
            }

            paragraph.Add(trimmed);
        }

        Flush();
        return new PostmortemDocument(title, statements.ToImmutable());
    }

    internal static PostmortemSection? SectionOf(string heading)
    {
        var text = Regex.Replace(heading.ToLowerInvariant(), @"[^a-z ]", " ").Trim();
        text = Regex.Replace(text, @"\s+", " ");
        if (text.Contains("root cause", StringComparison.Ordinal) || text is "cause" or "causes" || text.Contains("contributing factor", StringComparison.Ordinal))
        {
            return PostmortemSection.RootCause;
        }

        if (text.Contains("action item", StringComparison.Ordinal) || text is "actions" or "follow up" or "follow ups" or "remediation" or "corrective actions" or "preventive actions" or "prevention" or "next steps")
        {
            return PostmortemSection.ActionItems;
        }

        if (text.Contains("lesson", StringComparison.Ordinal) || text.Contains("what we learned", StringComparison.Ordinal) || text is "takeaways" or "learnings")
        {
            return PostmortemSection.Lessons;
        }

        return null;
    }

    private static void Extract(PostmortemSection section, string heading, int line, string unit, ImmutableArray<PostmortemStatement>.Builder statements)
    {
        foreach (var raw in SentenceBreak().Split(CandidateText.PlainText(unit)))
        {
            var original = Owner().Replace(raw.Trim(), string.Empty).Trim();
            if (original.Length < MinSentenceLength || original.Length > MaxSentenceLength || original.EndsWith('?'))
            {
                continue;
            }

            if (Rewrite(original, section) is { } hit)
            {
                statements.Add(new PostmortemStatement(section, heading, line, original, hit.Statement, hit.Confidence, hit.Rewritten));
            }
        }
    }

    internal static (string Statement, double Confidence, bool Rewritten)? Rewrite(string sentence, PostmortemSection section)
    {
        var weight = section switch { PostmortemSection.Lessons => 0.6, PostmortemSection.ActionItems => 0.55, _ => 0.5 };
        if (Modal().IsMatch(sentence))
        {
            return (sentence, weight, false);
        }

        var softened = Soft().Replace(sentence, m => m.Value.Contains("not", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("n't", StringComparison.OrdinalIgnoreCase) ? "must not" : "must", 1);
        if (softened != sentence)
        {
            return (softened, weight - 0.1, true);
        }

        if (section == PostmortemSection.ActionItems)
        {
            var first = sentence.Split(' ', 2)[0].ToLowerInvariant();
            if (ImperativeVerbs.Contains(first) && sentence.Contains(' ', StringComparison.Ordinal))
            {
                var rest = char.ToLowerInvariant(sentence[0]) + sentence[1..];
                return ("Teams must " + rest.TrimEnd('.', ' ') + ".", 0.4, true);
            }
        }

        return null;
    }

    private static string Slug(string heading) => Regex.Replace(heading.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    public static string SectionAnchor(PostmortemStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return Slug(statement.SectionHeading);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?(.+)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"(?<=[.!])\s+(?=[A-Z])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\s*\((?:owner|owners|assignee|assigned to|due|eta)\s*:[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Owner();

    [GeneratedRegex(@"\b(?:must\s+not|must|never|always)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Modal();

    [GeneratedRegex(@"\b(?:should\s+not|shouldn't|should|needs?\s+to|has\s+to|have\s+to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Soft();
}
