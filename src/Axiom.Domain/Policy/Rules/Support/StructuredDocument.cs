using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Axiom.Domain.Policy.Rules.Support;

internal enum ScalarKind
{
    Null,
    Text,
    Number,
    Boolean,
}

/// <summary>A node of a parsed JSON or YAML document, carrying the 1-based source line it started on.</summary>
internal abstract class DocNode(int line)
{
    public int Line { get; } = line;
}

internal sealed record DocEntry(string Key, int Line, DocNode Value);

internal sealed class DocMap(int line, IReadOnlyList<DocEntry> entries) : DocNode(line)
{
    public IReadOnlyList<DocEntry> Entries { get; } = entries;

    /// <summary>The value of the last entry with this key (later duplicates win, as in JSON and YAML loaders).</summary>
    public DocNode? Get(string key) => Entry(key)?.Value;

    public DocEntry? Entry(string key)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Entries[i].Key, key, StringComparison.Ordinal))
            {
                return Entries[i];
            }
        }

        return null;
    }

    public DocMap? Map(string key) => Get(key) as DocMap;

    public DocList? List(string key) => Get(key) as DocList;

    public string? Text(string key) => (Get(key) as DocScalar)?.Text;
}

internal sealed class DocList(int line, IReadOnlyList<DocNode> items) : DocNode(line)
{
    public IReadOnlyList<DocNode> Items { get; } = items;
}

internal sealed class DocScalar(int line, string? text, ScalarKind kind) : DocNode(line)
{
    public string? Text { get; } = text;

    public ScalarKind Kind { get; } = kind;

    public bool IsTrue => Kind == ScalarKind.Boolean && string.Equals(Text, "true", StringComparison.OrdinalIgnoreCase);

    public bool IsFalse => Kind == ScalarKind.Boolean && string.Equals(Text, "false", StringComparison.OrdinalIgnoreCase);

    public bool IsEmpty => Kind == ScalarKind.Null || string.IsNullOrWhiteSpace(Text);

    public double? Number =>
        Kind == ScalarKind.Number && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Reads JSON and YAML text into <see cref="DocNode"/> trees. Malformed input raises <see cref="FormatException"/>.</summary>
internal static class StructuredDocument
{
    public static bool IsJsonPath(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>All documents in the file: one for JSON, one per <c>---</c>-separated document for YAML.</summary>
    public static IReadOnlyList<DocNode> Parse(string path, string content) =>
        IsJsonPath(path) ? [ParseJson(content)] : YamlReader.ParseDocuments(content);

    public static DocNode ParseJson(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        var newlines = new List<long>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                newlines.Add(i);
            }
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = PolicyLimits.MaxNestingDepth,
        });

        try
        {
            if (!reader.Read())
            {
                throw new FormatException("The JSON document is empty.");
            }

            var root = ReadJsonValue(ref reader, newlines);
            if (reader.Read())
            {
                throw new FormatException("The JSON document has content after its root value.");
            }

            return root;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The JSON document is malformed near line {(ex.LineNumber ?? 0) + 1}.", ex);
        }
    }

    /// <summary>
    /// Resolves a dotted key path. Keys may themselves contain dots (for example the annotation
    /// <c>backstage.io/owner</c>): at every map the longest matching key is tried first.
    /// </summary>
    public static DocNode? Resolve(DocNode? node, string dottedPath)
    {
        if (dottedPath.Length == 0)
        {
            return node;
        }

        if (node is not DocMap map)
        {
            return null;
        }

        var end = dottedPath.Length;
        while (end > 0)
        {
            var value = map.Get(dottedPath[..end]);
            if (value is not null)
            {
                var resolved = end == dottedPath.Length ? value : Resolve(value, dottedPath[(end + 1)..]);
                if (resolved is not null)
                {
                    return resolved;
                }
            }

            end = dottedPath.LastIndexOf('.', end - 1);
        }

        return null;
    }

    /// <summary>True when the node is present and carries a value: not null, not blank, not an empty container.</summary>
    public static bool HasValue(DocNode? node) => node switch
    {
        null => false,
        DocScalar scalar => !scalar.IsEmpty,
        DocList list => list.Items.Count > 0,
        DocMap map => map.Entries.Count > 0,
        _ => false,
    };

    private static DocNode ReadJsonValue(ref Utf8JsonReader reader, List<long> newlines)
    {
        var line = LineOf(reader.TokenStartIndex, newlines);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var entries = new List<DocEntry>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString() ?? string.Empty;
                    var keyLine = LineOf(reader.TokenStartIndex, newlines);
                    reader.Read();
                    entries.Add(new DocEntry(key, keyLine, ReadJsonValue(ref reader, newlines)));
                }

                return new DocMap(line, entries);

            case JsonTokenType.StartArray:
                var items = new List<DocNode>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadJsonValue(ref reader, newlines));
                }

                return new DocList(line, items);

            case JsonTokenType.String:
                return new DocScalar(line, reader.GetString(), ScalarKind.Text);

            case JsonTokenType.Number:
                return new DocScalar(line, Encoding.UTF8.GetString(reader.ValueSpan), ScalarKind.Number);

            case JsonTokenType.True:
                return new DocScalar(line, "true", ScalarKind.Boolean);

            case JsonTokenType.False:
                return new DocScalar(line, "false", ScalarKind.Boolean);

            case JsonTokenType.Null:
                return new DocScalar(line, null, ScalarKind.Null);

            default:
                throw new FormatException($"Unexpected JSON token {reader.TokenType} at line {line}.");
        }
    }

    private static int LineOf(long byteOffset, List<long> newlines)
    {
        var index = newlines.BinarySearch(byteOffset);
        return (index < 0 ? ~index : index) + 1;
    }
}
