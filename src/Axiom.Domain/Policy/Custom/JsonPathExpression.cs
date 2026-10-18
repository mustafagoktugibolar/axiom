using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Axiom.Domain.Policy.Custom;

/// <summary>
/// Minimal JSON path: <c>$</c> followed by <c>.name</c>, <c>['name']</c>, <c>[index]</c> and <c>[*]</c>
/// segments. No filters, recursion or functions, so evaluation cost is bounded by the document size.
/// </summary>
internal sealed class JsonPathExpression
{
    private readonly ImmutableArray<Segment> _segments;

    private JsonPathExpression(ImmutableArray<Segment> segments)
    {
        _segments = segments;
    }

    private enum SegmentKind
    {
        Property,
        Index,
        Wildcard,
    }

    public static JsonPathExpression Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path[0] != '$')
        {
            throw new CustomRuleException("A JSON path must start with '$'.");
        }

        var segments = ImmutableArray.CreateBuilder<Segment>();
        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var start = ++i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }

                if (i == start)
                {
                    throw new CustomRuleException("Empty property name in JSON path.");
                }

                segments.Add(new Segment(SegmentKind.Property, path[start..i], 0));
            }
            else if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                {
                    throw new CustomRuleException("Unclosed '[' in JSON path.");
                }

                var inner = path[(i + 1)..close];
                i = close + 1;
                if (inner == "*")
                {
                    segments.Add(new Segment(SegmentKind.Wildcard, string.Empty, 0));
                }
                else if (inner.Length >= 2 && inner[0] == '\'' && inner[^1] == '\'')
                {
                    segments.Add(new Segment(SegmentKind.Property, inner[1..^1], 0));
                }
                else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    segments.Add(new Segment(SegmentKind.Index, string.Empty, index));
                }
                else
                {
                    throw new CustomRuleException("Unsupported bracket expression in JSON path.");
                }
            }
            else
            {
                throw new CustomRuleException("Unexpected character in JSON path.");
            }

            if (segments.Count > 32)
            {
                throw new CustomRuleException("JSON path is too long.");
            }
        }

        return new JsonPathExpression(segments.ToImmutable());
    }

    /// <summary>Every node the path selects. <paramref name="visit"/> is called once per visited node.</summary>
    public List<JsonElement> Select(JsonElement root, Action visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var current = new List<JsonElement> { root };
        foreach (var segment in _segments)
        {
            var next = new List<JsonElement>();
            foreach (var node in current)
            {
                visit();
                switch (segment.Kind)
                {
                    case SegmentKind.Property:
                        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment.Name, out var child))
                        {
                            next.Add(child);
                        }

                        break;
                    case SegmentKind.Index:
                        if (node.ValueKind == JsonValueKind.Array && segment.Index < node.GetArrayLength())
                        {
                            next.Add(node[segment.Index]);
                        }

                        break;
                    default:
                        if (node.ValueKind == JsonValueKind.Array)
                        {
                            next.AddRange(node.EnumerateArray());
                        }

                        break;
                }
            }

            current = next;
        }

        return current;
    }

    private readonly record struct Segment(SegmentKind Kind, string Name, int Index);
}
