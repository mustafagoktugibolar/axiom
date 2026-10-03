namespace Axiom.Domain.Governance;

/// <summary>
/// Segment-oriented path glob. <c>**</c> matches any number of segments (including none),
/// <c>*</c> matches any run of characters inside one segment, <c>?</c> matches one character.
/// Matching is ordinal and case-sensitive; separators are normalized to <c>/</c>.
/// </summary>
public sealed class GlobPattern : IEquatable<GlobPattern>
{
    private const string AnyDepth = "**";
    private readonly string[] _segments;

    private GlobPattern(string normalized, string[] segments)
    {
        Value = normalized;
        _segments = segments;
    }

    public string Value { get; }

    public bool HasWildcards => Value.AsSpan().IndexOfAny('*', '?') >= 0;

    public static GlobPattern Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var normalized = NormalizePath(pattern);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Glob pattern must contain at least one segment.", nameof(pattern));
        }

        return new GlobPattern(normalized, normalized.Split('/'));
    }

    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Where(p => p != "."));
    }

    public bool IsMatch(string path)
    {
        var target = NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegments(0, target, 0);
    }

    /// <summary>True when at least one concrete path could match both patterns.</summary>
    public bool MayIntersect(GlobPattern other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Intersects(_segments, 0, other._segments, 0);
    }

    /// <summary>
    /// Conservative containment: true only when every path matched by <paramref name="other"/>
    /// is provably matched by this pattern. May return false for patterns that are in fact contained.
    /// </summary>
    public bool Contains(GlobPattern other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ContainsFrom(0, other._segments, 0);
    }

    private bool MatchSegments(int p, string[] target, int t)
    {
        while (p < _segments.Length)
        {
            if (_segments[p] == AnyDepth)
            {
                for (var skip = t; skip <= target.Length; skip++)
                {
                    if (MatchSegments(p + 1, target, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (t >= target.Length || !SegmentMatches(_segments[p], target[t]))
            {
                return false;
            }

            p++;
            t++;
        }

        return t == target.Length;
    }

    private static bool SegmentMatches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Intersects(string[] a, int i, string[] b, int j)
    {
        if (i == a.Length && j == b.Length)
        {
            return true;
        }

        if (i < a.Length && a[i] == AnyDepth)
        {
            return Intersects(a, i + 1, b, j) || (j < b.Length && Intersects(a, i, b, j + 1));
        }

        if (j < b.Length && b[j] == AnyDepth)
        {
            return Intersects(a, i, b, j + 1) || (i < a.Length && Intersects(a, i + 1, b, j));
        }

        if (i == a.Length || j == b.Length)
        {
            return false;
        }

        return SegmentsIntersect(a[i], 0, b[j], 0) && Intersects(a, i + 1, b, j + 1);
    }

    private static bool SegmentsIntersect(string a, int i, string b, int j)
    {
        if (i == a.Length && j == b.Length)
        {
            return true;
        }

        if (i < a.Length && a[i] == '*')
        {
            return SegmentsIntersect(a, i + 1, b, j) || (j < b.Length && SegmentsIntersect(a, i, b, j + 1));
        }

        if (j < b.Length && b[j] == '*')
        {
            return SegmentsIntersect(a, i, b, j + 1) || (i < a.Length && SegmentsIntersect(a, i + 1, b, j));
        }

        if (i == a.Length || j == b.Length)
        {
            return false;
        }

        return (a[i] == '?' || b[j] == '?' || a[i] == b[j]) && SegmentsIntersect(a, i + 1, b, j + 1);
    }

    private bool ContainsFrom(int i, string[] other, int j)
    {
        if (i == _segments.Length)
        {
            return j == other.Length;
        }

        if (_segments[i] == AnyDepth)
        {
            return ContainsFrom(i + 1, other, j) || (j < other.Length && ContainsFrom(i, other, j + 1));
        }

        if (j == other.Length || other[j] == AnyDepth)
        {
            return false;
        }

        return SegmentContains(_segments[i], other[j]) && ContainsFrom(i + 1, other, j + 1);
    }

    private static bool SegmentContains(string outer, string inner)
    {
        if (outer == inner || outer == "*")
        {
            return true;
        }

        var innerIsLiteral = inner.AsSpan().IndexOfAny('*', '?') < 0;
        return innerIsLiteral && SegmentMatches(outer, inner);
    }

    public bool Equals(GlobPattern? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as GlobPattern);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
