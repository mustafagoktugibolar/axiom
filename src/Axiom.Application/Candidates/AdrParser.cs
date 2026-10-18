using System.Text.RegularExpressions;

namespace Axiom.Application.Candidates;

public enum AdrFormat
{
    Nygard,
    Madr,
}

public enum AdrStatus
{
    Unknown,
    Proposed,
    Accepted,
    Rejected,
    Deprecated,
    Superseded,
}

/// <summary>An architecture decision record read from markdown. Sections are trimmed text; missing ones are empty.</summary>
public sealed record AdrDocument(
    string Title,
    AdrStatus Status,
    string RawStatus,
    string Context,
    string Decision,
    string Consequences,
    AdrFormat Format,
    string? Decider);

/// <summary>
/// Deterministic reader for the two common ADR layouts: Nygard (<c>Status</c>, <c>Context</c>, <c>Decision</c>,
/// <c>Consequences</c> headings) and MADR (YAML front matter or a <c>Status</c> bullet, <c>Context and Problem
/// Statement</c>, <c>Decision Outcome</c>, <c>Consequences</c>). Pure text: no I/O, no model, same input same output.
/// </summary>
public static partial class AdrParser
{
    public const string NygardMethod = "adr-markdown/nygard@1";
    public const string MadrMethod = "adr-markdown/madr@1";

    private static readonly string[] IgnoredNames = ["readme", "index", "template", "adr-template", "0000-template", "_template", "toc", "contributing"];

    public static string MethodFor(AdrFormat format) => format == AdrFormat.Madr ? MadrMethod : NygardMethod;

    /// <summary>True for markdown files that live in a conventional ADR directory and are not an index or template.</summary>
    public static bool IsAdrPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = normalized.Split('/');
        var name = Path.GetFileNameWithoutExtension(segments[^1]).ToLowerInvariant();
        if (IgnoredNames.Contains(name) || name.Contains("template", StringComparison.Ordinal))
        {
            return false;
        }

        return segments[..^1].Any(s => s.ToLowerInvariant() is "adr" or "adrs" or "decisions" or "decision-records" or "architecture-decisions" or "doc-adr");
    }

    /// <summary>Returns the parsed ADR, or null when the text has no title or no decision to extract.</summary>
    public static AdrDocument? Parse(string path, string content, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hasFront = false;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                hasFront = true;
                ReadFrontMatter(text[4..end], front);
                var after = text.IndexOf('\n', end + 1);
                text = after < 0 ? string.Empty : text[(after + 1)..];
            }
        }

        var lines = text.Split('\n');
        var headings = FindHeadings(lines);
        var title = headings.FirstOrDefault(h => h.Level == 1)?.Text;
        title = Clean(NumberPrefix().Replace(title ?? (front.TryGetValue("title", out var t) ? t : string.Empty), string.Empty));
        if (title.Length == 0)
        {
            skipReason = "no title heading";
            return null;
        }

        string Section(string kind) => headings
            .Select((h, i) => (Heading: h, Index: i))
            .Where(x => Classify(x.Heading.Text) == kind)
            .Select(x => Body(lines, headings, x.Index))
            .FirstOrDefault(b => b.Length > 0) ?? string.Empty;

        var decision = Section("decision");
        if (decision.Length == 0)
        {
            skipReason = "no decision section";
            return null;
        }

        var rawStatus = front.TryGetValue("status", out var fs) && fs.Length > 0 ? fs : FirstLine(Section("status"));
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            rawStatus = StatusLine().Match(string.Join('\n', lines.Take(60))) is { Success: true } m ? m.Groups[1].Value : string.Empty;
        }

        rawStatus = Clean(rawStatus);
        var madr = hasFront || headings.Any(h => Classify(h.Text) == "decision" && h.Text.Contains("outcome", StringComparison.OrdinalIgnoreCase))
            || headings.Any(h => h.Text.Contains("considered options", StringComparison.OrdinalIgnoreCase) || h.Text.Contains("problem statement", StringComparison.OrdinalIgnoreCase));
        var decider = front.TryGetValue("decision-makers", out var dm) ? dm : front.GetValueOrDefault("deciders");

        skipReason = null;
        return new AdrDocument(title, MapStatus(rawStatus), rawStatus, Section("context"), decision, Section("consequences"), madr ? AdrFormat.Madr : AdrFormat.Nygard,
            string.IsNullOrWhiteSpace(decider) ? null : decider);
    }

    internal static AdrStatus MapStatus(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        return s switch
        {
            _ when s.StartsWith("accepted", StringComparison.Ordinal) || s.StartsWith("approved", StringComparison.Ordinal)
                || s.StartsWith("adopted", StringComparison.Ordinal) || s.StartsWith("decided", StringComparison.Ordinal) => AdrStatus.Accepted,
            _ when s.StartsWith("proposed", StringComparison.Ordinal) || s.StartsWith("draft", StringComparison.Ordinal)
                || s.StartsWith("pending", StringComparison.Ordinal) || s.StartsWith("rfc", StringComparison.Ordinal) => AdrStatus.Proposed,
            _ when s.StartsWith("rejected", StringComparison.Ordinal) => AdrStatus.Rejected,
            _ when s.StartsWith("deprecated", StringComparison.Ordinal) || s.StartsWith("obsolete", StringComparison.Ordinal) => AdrStatus.Deprecated,
            _ when s.StartsWith("superseded", StringComparison.Ordinal) || s.StartsWith("replaced", StringComparison.Ordinal) => AdrStatus.Superseded,
            _ => AdrStatus.Unknown,
        };
    }

    private sealed record Heading(int Line, int Level, string Text);

    private static List<Heading> FindHeadings(string[] lines)
    {
        var result = new List<Heading>();
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced && HeadingLine().Match(line) is { Success: true } m)
            {
                result.Add(new Heading(i, m.Groups[1].Length, m.Groups[2].Value.Trim()));
            }
        }

        return result;
    }

    private static string? ClassifyName(string text)
    {
        var name = LeadingNoise().Replace(text.ToLowerInvariant(), string.Empty).Trim();
        return name switch
        {
            "status" => "status",
            _ when name.StartsWith("context", StringComparison.Ordinal) => "context",
            "decision" or "decision outcome" or "the decision" or "our decision" => "decision",
            _ when name.StartsWith("consequences", StringComparison.Ordinal) => "consequences",
            _ => null,
        };
    }

    private static string? Classify(string text) => ClassifyName(text);

    /// <summary>Text under heading <paramref name="index"/> up to the next heading that is not deeper or that starts another known section.</summary>
    private static string Body(string[] lines, List<Heading> headings, int index)
    {
        var start = headings[index].Line + 1;
        var end = lines.Length;
        for (var j = index + 1; j < headings.Count; j++)
        {
            if (headings[j].Level <= headings[index].Level || Classify(headings[j].Text) is not null)
            {
                end = headings[j].Line;
                break;
            }
        }

        return string.Join('\n', lines[start..end]).Trim();
    }

    private static void ReadFrontMatter(string yaml, Dictionary<string, string> into)
    {
        string? list = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (list is not null && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                if (!into.ContainsKey(list))
                {
                    into[list] = Clean(line.TrimStart()[2..]);
                }

                continue;
            }

            list = null;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                list = key;
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                value = value[1..^1].Split(',')[0];
            }
            else if (key.Equals("decision-makers", StringComparison.OrdinalIgnoreCase) || key.Equals("deciders", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Split(',')[0];
            }

            into[key] = Clean(value);
        }
    }

    private static string FirstLine(string section) =>
        section.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    private static string Clean(string value) => value.Trim().Trim('*', '_', '`', '"', '\'', '[', ']').Trim();

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*(?:[-*]\s+)?\**status\**\s*:\s*\**\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^\s*(?:adr[-\s]?)?\d+\s*[.:)\-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPrefix();

    [GeneratedRegex(@"^[\d.\s:)\-]+")]
    private static partial Regex LeadingNoise();
}
