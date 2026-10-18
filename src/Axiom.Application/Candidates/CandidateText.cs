using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Application.Candidates;

/// <summary>Text helpers shared by the deterministic candidate extractors. Pure functions.</summary>
internal static partial class CandidateText
{
    public static string Collapse(string text) => Whitespace().Replace(text.Trim(), " ");

    /// <summary>Hash of whitespace-normalized, case-folded text, so reformatting does not create a duplicate candidate.</summary>
    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Collapse(text).ToLowerInvariant())));

    public static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>Source locator that fits the 256-character identifier limit, keeping the tail of long paths.</summary>
    public static string Locator(string repository, string path, string suffix, string commitSha)
    {
        var shortSha = commitSha.Length > 12 ? commitSha[..12] : commitSha;
        var tail = $"{suffix}@{shortSha}";
        var head = $"{repository}:";
        var room = Math.Max(8, 250 - head.Length - tail.Length);
        var shownPath = path.Length <= room ? path : "…" + path[^(room - 1)..];
        return head + shownPath + tail;
    }

    /// <summary>Strips markdown emphasis, links and inline code to plain sentence text.</summary>
    public static string PlainText(string markdown)
    {
        var text = MarkdownLink().Replace(markdown, "$1");
        text = text.Replace("`", string.Empty, StringComparison.Ordinal).Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal);
        return Collapse(text);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();
}
