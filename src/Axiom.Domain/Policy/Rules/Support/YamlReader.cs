using System.Globalization;
using System.Text;

namespace Axiom.Domain.Policy.Rules.Support;

/// <summary>
/// A small, dependency-free reader for the YAML subset used by configuration, manifests and API
/// contracts: block mappings and sequences, flow collections, quoted and plain scalars, block
/// scalars, comments and multiple documents. Anchors and tags are skipped, aliases are read as plain
/// text, and Helm template directive lines (<c>{{ ... }}</c>) are ignored. Structure it cannot place
/// raises <see cref="FormatException"/> so callers fail closed instead of evaluating a partial document.
/// </summary>
internal static class YamlReader
{
    public static IReadOnlyList<DocNode> ParseDocuments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(text).ParseAll();
    }

    private readonly record struct Line(int Number, int RawIndex, int Indent, string Content);

    private sealed class Parser
    {
        private readonly string[] _raw;
        private readonly List<Line> _lines = [];
        private int _index;

        public Parser(string text)
        {
            _raw = ChangeSet.SplitLines(text);
            for (var i = 0; i < _raw.Length; i++)
            {
                var raw = _raw[i];
                var indent = 0;
                while (indent < raw.Length && (raw[indent] == ' ' || raw[indent] == '\t'))
                {
                    indent++;
                }

                var content = StripComment(raw.AsSpan(indent)).TrimEnd();
                if (content.Length == 0
                    || content[0] == '%'
                    || (content.StartsWith("{{", StringComparison.Ordinal) && content.EndsWith("}}", StringComparison.Ordinal)))
                {
                    continue;
                }

                _lines.Add(new Line(i + 1, i, indent, content));
            }
        }

        private bool AtEnd => _index >= _lines.Count;

        private Line Current => _lines[_index];

        public List<DocNode> ParseAll()
        {
            var documents = new List<DocNode>();
            while (!AtEnd)
            {
                if (IsSeparator(Current))
                {
                    _index++;
                    continue;
                }

                documents.Add(ParseBlock(0, 0));
                if (!AtEnd && !IsSeparator(Current))
                {
                    throw new FormatException($"Unsupported YAML structure at line {Current.Number}.");
                }
            }

            return documents;
        }

        private static bool IsSeparator(Line line) =>
            line.Indent == 0
            && (line.Content is "---" or "..." || line.Content.StartsWith("--- ", StringComparison.Ordinal));

        private static bool IsSequenceEntry(string content) =>
            content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

        private bool HasBlockAt(int minimumIndent) => !AtEnd && !IsSeparator(Current) && Current.Indent >= minimumIndent;

        private DocNode ParseBlock(int minimumIndent, int depth)
        {
            if (depth > PolicyLimits.MaxNestingDepth)
            {
                throw new FormatException("The YAML document is nested too deeply.");
            }

            if (!HasBlockAt(minimumIndent))
            {
                return new DocScalar(AtEnd ? _raw.Length : Current.Number, null, ScalarKind.Null);
            }

            var line = Current;
            if (IsSequenceEntry(line.Content))
            {
                return ParseSequence(line.Indent, depth);
            }

            if (StartsFlow(line.Content))
            {
                _index++;
                return ReadFlow(line.Content, line.Number);
            }

            if (TrySplitKey(line.Content, out _, out _))
            {
                return ParseMap(line.Indent, depth);
            }

            _index++;
            var text = new StringBuilder(line.Content);
            while (HasBlockAt(line.Indent + 1))
            {
                text.Append(' ').Append(Current.Content);
                _index++;
            }

            return ParseScalar(text.ToString(), line.Number);
        }

        private DocList ParseSequence(int indent, int depth)
        {
            var first = Current.Number;
            var items = new List<DocNode>();
            while (HasBlockAt(indent) && Current.Indent == indent && IsSequenceEntry(Current.Content))
            {
                var line = Current;
                var rest = line.Content[1..];
                var trimmed = rest.TrimStart();
                if (trimmed.Length == 0)
                {
                    _index++;
                    items.Add(ParseBlock(indent + 1, depth + 1));
                }
                else
                {
                    // Re-read the remainder of the entry as a line of its own, indented to where it starts,
                    // so "- name: a" followed by "  image: b" forms one mapping.
                    var itemIndent = indent + 1 + (rest.Length - trimmed.Length);
                    _lines[_index] = line with { Indent = itemIndent, Content = trimmed };
                    items.Add(ParseBlock(itemIndent, depth + 1));
                }

                SkipDeeperThan(indent);
            }

            return new DocList(first, items);
        }

        private DocMap ParseMap(int indent, int depth)
        {
            var first = Current.Number;
            var entries = new List<DocEntry>();
            while (HasBlockAt(indent)
                && Current.Indent == indent
                && !IsSequenceEntry(Current.Content)
                && TrySplitKey(Current.Content, out var key, out var rest))
            {
                var line = Current;
                _index++;
                rest = StripProperties(rest);

                DocNode value;
                if (rest.Length == 0)
                {
                    if (HasBlockAt(indent + 1))
                    {
                        value = ParseBlock(indent + 1, depth + 1);
                    }
                    else if (HasBlockAt(indent) && Current.Indent == indent && IsSequenceEntry(Current.Content))
                    {
                        value = ParseSequence(indent, depth + 1);
                    }
                    else
                    {
                        value = new DocScalar(line.Number, null, ScalarKind.Null);
                    }
                }
                else if (rest[0] is '|' or '>')
                {
                    value = ReadBlockScalar(line, indent);
                }
                else if (StartsFlow(rest))
                {
                    value = ReadFlow(rest, line.Number);
                }
                else
                {
                    var text = new StringBuilder(rest);
                    while (HasBlockAt(indent + 1) && !IsQuoted(rest))
                    {
                        text.Append(' ').Append(Current.Content);
                        _index++;
                    }

                    value = ParseScalar(text.ToString(), line.Number);
                }

                entries.Add(new DocEntry(key, line.Number, value));
                SkipDeeperThan(indent);
            }

            return new DocMap(first, entries);
        }

        /// <summary>Reads a flow collection that may continue over the following lines until its brackets close.</summary>
        private DocNode ReadFlow(string first, int lineNumber)
        {
            var flow = new StringBuilder(first);
            while (!IsBalanced(flow) && HasBlockAt(0))
            {
                flow.Append(' ').Append(Current.Content);
                _index++;
            }

            return ParseFlow(flow.ToString(), lineNumber);
        }

        private void SkipDeeperThan(int indent)
        {
            while (HasBlockAt(indent + 1))
            {
                _index++;
            }
        }

        private DocScalar ReadBlockScalar(Line header, int indent)
        {
            var text = new StringBuilder();
            var last = header.RawIndex;
            for (var i = header.RawIndex + 1; i < _raw.Length; i++)
            {
                var raw = _raw[i];
                if (raw.AsSpan().Trim().Length > 0 && IndentOf(raw) <= indent)
                {
                    break;
                }

                text.Append(raw.Trim()).Append('\n');
                last = i;
            }

            while (!AtEnd && Current.RawIndex <= last)
            {
                _index++;
            }

            return new DocScalar(header.Number, text.ToString().Trim(), ScalarKind.Text);
        }

        private static int IndentOf(string raw)
        {
            var indent = 0;
            while (indent < raw.Length && (raw[indent] == ' ' || raw[indent] == '\t'))
            {
                indent++;
            }

            return indent;
        }

        private static bool StartsFlow(string content) =>
            content[0] == '[' || (content[0] == '{' && !content.StartsWith("{{", StringComparison.Ordinal));

        private static bool IsQuoted(string content) => content[0] is '"' or '\'';

        /// <summary>Drops leading anchors (<c>&amp;name</c>) and tags (<c>!tag</c>) from a value.</summary>
        private static string StripProperties(string rest)
        {
            while (rest.Length > 0 && rest[0] is '&' or '!')
            {
                var space = rest.IndexOf(' ', StringComparison.Ordinal);
                rest = space < 0 ? string.Empty : rest[(space + 1)..].TrimStart();
            }

            return rest;
        }

        private static bool IsBalanced(StringBuilder flow)
        {
            var depth = 0;
            var quote = '\0';
            for (var i = 0; i < flow.Length; i++)
            {
                var c = flow[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c is '[' or '{')
                {
                    depth++;
                }
                else if (c is ']' or '}')
                {
                    depth--;
                }
            }

            return depth <= 0;
        }

        /// <summary>Removes a trailing <c># comment</c> that is not inside a quoted scalar.</summary>
        private static string StripComment(ReadOnlySpan<char> content)
        {
            var quote = '\0';
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'' && (i == 0 || char.IsWhiteSpace(content[i - 1]) || content[i - 1] is '[' or '{' or ',' or ':'))
                {
                    quote = c;
                }
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(content[i - 1])))
                {
                    return content[..i].ToString();
                }
            }

            return content.ToString();
        }

        /// <summary>Splits <c>key: value</c>. The key may be quoted; a plain key ends at the first <c>: </c> or trailing <c>:</c>.</summary>
        private static bool TrySplitKey(string content, out string key, out string rest)
        {
            key = string.Empty;
            rest = string.Empty;
            int colon;
            if (content[0] is '"' or '\'')
            {
                var close = ClosingQuote(content, 0);
                if (close < 0)
                {
                    return false;
                }

                colon = close + 1;
                while (colon < content.Length && content[colon] == ' ')
                {
                    colon++;
                }

                if (colon >= content.Length || content[colon] != ':')
                {
                    return false;
                }

                key = Unquote(content[..(close + 1)]);
            }
            else
            {
                colon = -1;
                for (var i = 0; i < content.Length; i++)
                {
                    if (content[i] == ':' && (i == content.Length - 1 || content[i + 1] == ' '))
                    {
                        colon = i;
                        break;
                    }
                }

                if (colon <= 0)
                {
                    return false;
                }

                key = content[..colon].TrimEnd();
                if (key.StartsWith("? ", StringComparison.Ordinal))
                {
                    key = key[2..].TrimStart();
                }
            }

            rest = content[(colon + 1)..].Trim();
            return true;
        }

        private static int ClosingQuote(string text, int open)
        {
            var quote = text[open];
            for (var i = open + 1; i < text.Length; i++)
            {
                if (quote == '"' && text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == quote)
                {
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    return i;
                }
            }

            return -1;
        }

        private static string Unquote(string quoted)
        {
            var inner = quoted[1..^1];
            if (quoted[0] == '\'')
            {
                return inner.Replace("''", "'", StringComparison.Ordinal);
            }

            if (!inner.Contains('\\', StringComparison.Ordinal))
            {
                return inner;
            }

            var result = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i == inner.Length - 1)
                {
                    result.Append(inner[i]);
                    continue;
                }

                i++;
                result.Append(inner[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    var other => other,
                });
            }

            return result.ToString();
        }

        private static DocScalar ParseScalar(string text, int line)
        {
            text = StripProperties(text.Trim());
            if (text.Length == 0)
            {
                return new DocScalar(line, null, ScalarKind.Null);
            }

            if (IsQuoted(text))
            {
                var close = ClosingQuote(text, 0);
                return new DocScalar(line, close < 0 ? text[1..] : Unquote(text[..(close + 1)]), ScalarKind.Text);
            }

            if (text is "~" or "null" or "Null" or "NULL")
            {
                return new DocScalar(line, null, ScalarKind.Null);
            }

            if (text is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
            {
                return new DocScalar(line, text.ToLowerInvariant(), ScalarKind.Boolean);
            }

            var numeric = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number)
                && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.');
            return new DocScalar(line, text, numeric ? ScalarKind.Number : ScalarKind.Text);
        }

        private static DocNode ParseFlow(string text, int line)
        {
            var position = 0;
            var node = ParseFlowValue(text, ref position, line, 0, stopAtColon: false);
            SkipSpaces(text, ref position);
            return position < text.Length
                ? throw new FormatException($"Unsupported YAML flow collection at line {line}.")
                : node;
        }

        private static void SkipSpaces(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }

        private static DocNode ParseFlowValue(string text, ref int position, int line, int depth, bool stopAtColon)
        {
            if (depth > PolicyLimits.MaxNestingDepth)
            {
                throw new FormatException("The YAML document is nested too deeply.");
            }

            SkipSpaces(text, ref position);
            if (position >= text.Length)
            {
                return new DocScalar(line, null, ScalarKind.Null);
            }

            if (text[position] == '[')
            {
                position++;
                var items = new List<DocNode>();
                while (true)
                {
                    SkipSpaces(text, ref position);
                    if (position >= text.Length)
                    {
                        throw new FormatException($"Unterminated YAML flow sequence at line {line}.");
                    }

                    if (text[position] == ']')
                    {
                        position++;
                        return new DocList(line, items);
                    }

                    if (text[position] == ',')
                    {
                        position++;
                        continue;
                    }

                    items.Add(ParseFlowValue(text, ref position, line, depth + 1, stopAtColon: false));
                }
            }

            if (text[position] == '{')
            {
                position++;
                var entries = new List<DocEntry>();
                while (true)
                {
                    SkipSpaces(text, ref position);
                    if (position >= text.Length)
                    {
                        throw new FormatException($"Unterminated YAML flow mapping at line {line}.");
                    }

                    if (text[position] == '}')
                    {
                        position++;
                        return new DocMap(line, entries);
                    }

                    if (text[position] == ',')
                    {
                        position++;
                        continue;
                    }

                    var key = ParseFlowValue(text, ref position, line, depth + 1, stopAtColon: true) as DocScalar;
                    SkipSpaces(text, ref position);
                    DocNode value = new DocScalar(line, null, ScalarKind.Null);
                    if (position < text.Length && text[position] == ':')
                    {
                        position++;
                        value = ParseFlowValue(text, ref position, line, depth + 1, stopAtColon: false);
                    }

                    entries.Add(new DocEntry(key?.Text ?? string.Empty, line, value));
                }
            }

            if (text[position] is '"' or '\'')
            {
                var close = ClosingQuote(text, position);
                if (close < 0)
                {
                    throw new FormatException($"Unterminated quoted YAML scalar at line {line}.");
                }

                var quoted = text[position..(close + 1)];
                position = close + 1;
                return new DocScalar(line, Unquote(quoted), ScalarKind.Text);
            }

            var start = position;
            while (position < text.Length
                && text[position] is not (',' or ']' or '}')
                && !(stopAtColon && text[position] == ':'))
            {
                position++;
            }

            return ParseScalar(text[start..position], line);
        }
    }
}
