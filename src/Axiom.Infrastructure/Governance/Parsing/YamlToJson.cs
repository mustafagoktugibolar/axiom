using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Axiom.Infrastructure.Governance.Parsing;

/// <summary>
/// Converts a YAML document to JSON using YAML core-schema typing for plain scalars only.
/// Quoted scalars always stay strings, and dates are never coerced, so the JSON is a faithful,
/// deterministic image of the source text.
/// </summary>
internal static class YamlToJson
{
    private static readonly System.Buffers.SearchValues<char> NumericChars = System.Buffers.SearchValues.Create("0123456789.eE+-");

    public static JsonNode? Convert(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0)
        {
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            throw new YamlException("A governance record must be a single YAML document.");
        }

        return ConvertNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ConvertNode(YamlNode node) => node switch
    {
        YamlMappingNode mapping => ConvertMapping(mapping),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ConvertNode).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => throw new YamlException($"Unsupported YAML node at {node.Start}; aliases and anchors are not allowed in governance records."),
    };

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            if (key is not YamlScalarNode { Value: { } name })
            {
                throw new YamlException($"Only scalar keys are supported (at {key.Start}).");
            }

            if (result.ContainsKey(name))
            {
                throw new YamlException($"Duplicate key '{name}' at {key.Start}.");
            }

            result[name] = ConvertNode(value);
        }

        return result;
    }

    private static JsonValue? ConvertScalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style is not ScalarStyle.Plain)
        {
            return JsonValue.Create(text);
        }

        return text switch
        {
            "" or "~" or "null" or "Null" or "NULL" => null,
            "true" or "True" or "TRUE" => JsonValue.Create(true),
            "false" or "False" or "FALSE" => JsonValue.Create(false),
            _ when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) => JsonValue.Create(integer),
            _ when LooksNumeric(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(text),
        };
    }

    private static bool LooksNumeric(string text) =>
        text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.') && text.AsSpan().IndexOfAnyExcept(NumericChars) < 0;
}
