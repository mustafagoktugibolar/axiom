using System.Text.RegularExpressions;
using Xunit;

namespace Axiom.UnitTests.Portal;

/// <summary>
/// Static guards over the Vue sources (plan 10.10). They cannot prove accessibility or XSS-freedom, but they
/// fail the build on the regressions that are cheap to detect: raw HTML injection, unlabeled controls,
/// images without alt text and data tables without a caption.
/// </summary>
public sealed partial class PortalStaticGuardTests
{
    private static string PortalRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Axiom.Portal", "src")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Axiom.Portal");
    }

    private static List<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(PortalRoot(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".vue", StringComparison.Ordinal) || f.EndsWith(".ts", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(PortalRoot(), f), File.ReadAllText(f)))
            .ToList();

    private static List<(string File, string Text)> Components() =>
        Sources().Where(s => s.File.EndsWith(".vue", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Sources_are_found() => Assert.True(Components().Count >= 10);

    [Fact]
    public void No_raw_html_injection()
    {
        var hits = Sources()
            .Where(s => Regex.IsMatch(s.Text, @"v-html|\.innerHTML|\.outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function\("))
            .Select(s => s.File).ToList();
        Assert.True(hits.Count == 0, "Raw HTML/script injection in: " + string.Join(", ", hits));
    }

    [Fact]
    public void Tokens_are_never_kept_in_localStorage()
    {
        var hits = Sources().Where(s => s.Text.Contains("localStorage", StringComparison.Ordinal)).Select(s => s.File).ToList();
        Assert.True(hits.Count == 0, "localStorage used in: " + string.Join(", ", hits));
    }

    [Fact]
    public void Images_have_alt_text()
    {
        var bad = Components()
            .SelectMany(s => Regex.Matches(s.Text, @"<img\b[^>]*>").Select(m => (s.File, m.Value)))
            .Where(t => !t.Value.Contains("alt=", StringComparison.Ordinal))
            .Select(t => t.File).ToList();
        Assert.True(bad.Count == 0, "<img> without alt in: " + string.Join(", ", bad));
    }

    [Fact]
    public void Form_controls_have_an_accessible_name()
    {
        var bad = new List<string>();
        foreach (var (file, text) in Components())
        {
            foreach (var m in ControlRegex().Matches(text).Cast<Match>())
            {
                var tag = m.Value;
                if (tag.Contains("type=\"hidden\"", StringComparison.Ordinal) || tag.Contains("aria-label", StringComparison.Ordinal))
                {
                    continue;
                }

                // Wrapped by a <label> that opens on this or the previous line and is still open.
                var lineStart = text.LastIndexOf('\n', m.Index) + 1;
                var prevStart = lineStart > 1 ? text.LastIndexOf('\n', lineStart - 2) + 1 : 0;
                var before = text[prevStart..m.Index];
                if (before.LastIndexOf("<label", StringComparison.Ordinal) > before.LastIndexOf("</label>", StringComparison.Ordinal))
                {
                    continue;
                }

                bad.Add($"{file}: {tag[..Math.Min(tag.Length, 70)]}");
            }
        }

        Assert.True(bad.Count == 0, "Controls without label or aria-label:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Tables_have_a_caption_and_scoped_headers()
    {
        var bad = new List<string>();
        foreach (var (file, text) in Components())
        {
            bad.AddRange(Regex.Matches(text, @"<table\b[^>]*>(.*?)</table>", RegexOptions.Singleline)
                .Where(m => !m.Value.Contains("<caption", StringComparison.Ordinal))
                .Select(_ => $"{file}: table without <caption>"));
            if (text.Contains("<th>", StringComparison.Ordinal))
            {
                bad.Add($"{file}: <th> without scope");
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Index_page_declares_language_and_does_not_block_zoom()
    {
        var html = File.ReadAllText(Path.Combine(PortalRoot(), "index.html"));
        Assert.Contains("<html lang=\"", html, StringComparison.Ordinal);
        Assert.Contains("width=device-width", html, StringComparison.Ordinal);
        Assert.DoesNotContain("user-scalable=no", html, StringComparison.Ordinal);
        Assert.DoesNotContain("maximum-scale", html, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<(input|textarea|select)\b[^>]*>")]
    private static partial Regex ControlRegex();
}
