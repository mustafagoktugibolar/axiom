namespace Axiom.Domain.Policy.Rules.Support;

/// <summary>A change that breaks existing consumers of a contract.</summary>
internal sealed record BreakingChange(string Message, string Location, int? Line);

/// <summary>
/// Structural comparison of two revisions of an OpenAPI or JSON Schema document. Local
/// <c>$ref</c>s are followed and <c>allOf</c> members are merged; <c>oneOf</c>/<c>anyOf</c> and
/// external references are not interpreted. Recursion is bounded by depth and by visited pairs.
/// </summary>
internal sealed class ContractComparer
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];
    private static readonly (string Keyword, bool IsUpperBound)[] Bounds =
    [
        ("maximum", true), ("maxLength", true), ("maxItems", true),
        ("minimum", false), ("minLength", false), ("minItems", false),
    ];

    private readonly DocMap _oldRoot;
    private readonly DocMap _newRoot;
    private readonly List<BreakingChange> _changes = [];
    private readonly HashSet<(DocMap Old, DocMap New, Direction Direction)> _visited = [];

    private ContractComparer(DocMap oldRoot, DocMap newRoot)
    {
        _oldRoot = oldRoot;
        _newRoot = newRoot;
    }

    private enum Direction
    {
        /// <summary>Data the consumer sends: new obligations and narrower accepted values break.</summary>
        Request,

        /// <summary>Data the consumer receives: lost guarantees break.</summary>
        Response,

        /// <summary>A standalone schema used both ways.</summary>
        Both,
    }

    public static bool IsOpenApi(DocMap root) => root.Get("openapi") is not null || root.Get("swagger") is not null;

    public static bool IsJsonSchema(DocMap root) =>
        root.Get("$schema") is not null || root.Get("properties") is not null || root.Get("type") is not null
        || root.Get("$defs") is not null || root.Get("definitions") is not null;

    public static IReadOnlyList<BreakingChange> CompareOpenApi(DocMap previous, DocMap current)
    {
        var comparer = new ContractComparer(previous, current);
        comparer.ComparePaths();
        return comparer._changes;
    }

    public static IReadOnlyList<BreakingChange> CompareJsonSchema(DocMap previous, DocMap current)
    {
        var comparer = new ContractComparer(previous, current);
        comparer.CompareSchema(previous, current, Direction.Both, "#", 0);
        foreach (var container in new[] { "$defs", "definitions" })
        {
            foreach (var definition in previous.Map(container)?.Entries ?? [])
            {
                var location = $"#/{container}/{definition.Key}";
                var replacement = current.Map(container)?.Get(definition.Key);
                if (replacement is null)
                {
                    comparer.Report($"Schema definition '{definition.Key}' was removed.", location, current.Line);
                }
                else
                {
                    comparer.CompareSchema(definition.Value, replacement, Direction.Both, location, 0);
                }
            }
        }

        return comparer._changes;
    }

    private void Report(string message, string location, int? line) => _changes.Add(new BreakingChange(message, location, line));

    private void ComparePaths()
    {
        var oldPaths = _oldRoot.Map("paths");
        var newPaths = _newRoot.Map("paths");
        if (oldPaths is null)
        {
            return;
        }

        var current = (newPaths?.Entries ?? [])
            .GroupBy(e => NormalizeTemplate(e.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (var path in oldPaths.Entries)
        {
            var location = $"paths.{path.Key}";
            if (!current.TryGetValue(NormalizeTemplate(path.Key), out var match))
            {
                Report($"Path '{path.Key}' was removed.", location, newPaths?.Line ?? _newRoot.Line);
                continue;
            }

            if (Deref(path.Value, _oldRoot) is not DocMap oldItem || Deref(match.Value, _newRoot) is not DocMap newItem)
            {
                continue;
            }

            foreach (var method in Methods)
            {
                if (oldItem.Map(method) is not { } oldOperation)
                {
                    continue;
                }

                var name = $"{method.ToUpperInvariant()} {path.Key}";
                if (newItem.Map(method) is not { } newOperation)
                {
                    Report($"Operation {name} was removed.", $"{location}.{method}", match.Line);
                    continue;
                }

                CompareOperation(name, $"{location}.{method}", oldItem, oldOperation, newItem, newOperation);
            }
        }
    }

    private static string NormalizeTemplate(string path)
    {
        var builder = new System.Text.StringBuilder(path.Length);
        var inParameter = false;
        foreach (var c in path)
        {
            if (c == '{')
            {
                inParameter = true;
                builder.Append("{}");
            }
            else if (c == '}')
            {
                inParameter = false;
            }
            else if (!inParameter)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private void CompareOperation(string name, string location, DocMap oldItem, DocMap oldOperation, DocMap newItem, DocMap newOperation)
    {
        var oldParameters = ParametersOf(oldItem, oldOperation, _oldRoot);
        var newParameters = ParametersOf(newItem, newOperation, _newRoot);

        foreach (var (key, parameter) in newParameters)
        {
            var parameterLocation = $"{location}.parameters.{key}";
            if (!oldParameters.TryGetValue(key, out var before))
            {
                if (IsRequiredParameter(parameter))
                {
                    Report($"{name}: required parameter '{key}' was added.", parameterLocation, parameter.Line);
                }

                continue;
            }

            if (IsRequiredParameter(parameter) && !IsRequiredParameter(before))
            {
                Report($"{name}: parameter '{key}' became required.", parameterLocation, parameter.Line);
            }

            CompareSchema(before.Get("schema") ?? before, parameter.Get("schema") ?? parameter, Direction.Request, parameterLocation, 0);
        }

        var oldBody = Deref(oldOperation.Get("requestBody"), _oldRoot) as DocMap;
        var newBody = Deref(newOperation.Get("requestBody"), _newRoot) as DocMap;
        if (newBody is not null && IsTrue(newBody.Get("required")) && (oldBody is null || !IsTrue(oldBody.Get("required"))))
        {
            Report($"{name}: the request body became required.", $"{location}.requestBody", newBody.Line);
        }

        if (oldBody is not null && newBody is not null)
        {
            CompareContent(name, "request", $"{location}.requestBody", oldBody, newBody, Direction.Request);
        }

        var oldResponses = oldOperation.Map("responses");
        var newResponses = newOperation.Map("responses");
        foreach (var response in oldResponses?.Entries ?? [])
        {
            var responseLocation = $"{location}.responses.{response.Key}";
            var after = newResponses?.Get(response.Key);
            if (after is null)
            {
                Report($"{name}: response {response.Key} was removed.", responseLocation, newResponses?.Line ?? newOperation.Line);
                continue;
            }

            if (Deref(response.Value, _oldRoot) is not DocMap oldResponse || Deref(after, _newRoot) is not DocMap newResponse)
            {
                continue;
            }

            CompareContent(name, $"response {response.Key}", responseLocation, oldResponse, newResponse, Direction.Response);
            if (oldResponse.Get("schema") is { } oldSchema && newResponse.Get("schema") is { } newSchema)
            {
                CompareSchema(oldSchema, newSchema, Direction.Response, responseLocation + ".schema", 0);
            }
        }
    }

    private void CompareContent(string name, string what, string location, DocMap previous, DocMap current, Direction direction)
    {
        var newContent = current.Map("content");
        foreach (var media in previous.Map("content")?.Entries ?? [])
        {
            var mediaLocation = $"{location}.content.{media.Key}";
            if (newContent?.Get(media.Key) is not DocMap after)
            {
                Report($"{name}: {what} media type '{media.Key}' was removed.", mediaLocation, newContent?.Line ?? current.Line);
                continue;
            }

            if (media.Value is DocMap before && before.Get("schema") is { } oldSchema && after.Get("schema") is { } newSchema)
            {
                CompareSchema(oldSchema, newSchema, direction, mediaLocation + ".schema", 0);
            }
        }
    }

    private static SortedDictionary<string, DocMap> ParametersOf(DocMap item, DocMap operation, DocMap root)
    {
        var parameters = new SortedDictionary<string, DocMap>(StringComparer.Ordinal);
        foreach (var source in new[] { item.List("parameters"), operation.List("parameters") })
        {
            foreach (var node in source?.Items ?? [])
            {
                if (Deref(node, root) is DocMap parameter && parameter.Text("name") is { } name)
                {
                    parameters[$"{parameter.Text("in") ?? "query"}:{name}"] = parameter;
                }
            }
        }

        return parameters;
    }

    private static bool IsRequiredParameter(DocMap parameter) => IsTrue(parameter.Get("required")) || parameter.Text("in") == "path";

    private static bool IsTrue(DocNode? node) => node is DocScalar { IsTrue: true };

    private void CompareSchema(DocNode? previous, DocNode? current, Direction direction, string location, int depth)
    {
        if (depth > PolicyLimits.MaxNestingDepth
            || Deref(previous, _oldRoot) is not DocMap oldMap
            || Deref(current, _newRoot) is not DocMap newMap
            || !_visited.Add((oldMap, newMap, direction)))
        {
            return;
        }

        var before = SchemaView.Of(oldMap, _oldRoot);
        var after = SchemaView.Of(newMap, _newRoot);
        var line = newMap.Line;

        if (before.Type is not null && after.Type is not null && before.Type != after.Type)
        {
            Report($"Type changed from '{before.Type}' to '{after.Type}'.", location, line);
        }
        else if (before.Format is not null && after.Format is not null && before.Format != after.Format)
        {
            Report($"Format changed from '{before.Format}' to '{after.Format}'.", location, line);
        }

        if (before.Enum is not null && after.Enum is not null)
        {
            foreach (var value in before.Enum.Where(v => !after.Enum.Contains(v)))
            {
                Report($"Enum value '{value}' was removed.", location, line);
            }
        }
        else if (before.Enum is null && after.Enum is not null && direction != Direction.Response)
        {
            Report("Accepted values were narrowed to an enum.", location, line);
        }

        if (direction != Direction.Response)
        {
            foreach (var (keyword, isUpperBound) in Bounds)
            {
                var was = before.Bound(keyword);
                var now = after.Bound(keyword);
                if (now is not null && (was is null || (isUpperBound ? now < was : now > was)))
                {
                    Report($"Constraint '{keyword}' was narrowed{(was is null ? string.Empty : $" from {was}")} to {now}.", location, line);
                }
            }

            foreach (var name in after.Required.Where(r => !before.Required.Contains(r)))
            {
                Report(
                    before.Properties.ContainsKey(name) ? $"Property '{name}' became required." : $"Required property '{name}' was added.",
                    $"{location}.properties.{name}",
                    after.Properties.TryGetValue(name, out var added) ? added.Line : line);
            }
        }

        foreach (var name in before.Properties.Keys.Where(n => !after.Properties.ContainsKey(n)))
        {
            if (before.Required.Contains(name) && direction != Direction.Request)
            {
                Report($"Required property '{name}' was removed.", $"{location}.properties.{name}", line);
            }
            else if (direction == Direction.Both)
            {
                Report($"Property '{name}' was removed.", $"{location}.properties.{name}", line);
            }
        }

        if (direction == Direction.Response)
        {
            foreach (var name in before.Required.Where(r => after.Properties.ContainsKey(r) && !after.Required.Contains(r)))
            {
                Report($"Property '{name}' is no longer guaranteed (was required).", $"{location}.properties.{name}", after.Properties[name].Line);
            }
        }

        foreach (var (name, schema) in before.Properties)
        {
            if (after.Properties.TryGetValue(name, out var counterpart))
            {
                CompareSchema(schema, counterpart, direction, $"{location}.properties.{name}", depth + 1);
            }
        }

        if (before.Items is not null && after.Items is not null)
        {
            CompareSchema(before.Items, after.Items, direction, location + ".items", depth + 1);
        }
    }

    /// <summary>Follows local <c>#/...</c> references; anything else is returned unchanged.</summary>
    private static DocNode? Deref(DocNode? node, DocMap root)
    {
        for (var hops = 0; hops < 16 && node is DocMap map && map.Text("$ref") is { } reference; hops++)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                return node;
            }

            DocNode? target = root;
            foreach (var token in reference[2..].Split('/'))
            {
                var key = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                target = (target as DocMap)?.Get(key);
            }

            if (target is null)
            {
                return node;
            }

            node = target;
        }

        return node;
    }

    private sealed class SchemaView
    {
        private readonly Dictionary<string, double> _bounds = new(StringComparer.Ordinal);

        public string? Type { get; private set; }

        public string? Format { get; private set; }

        public SortedSet<string>? Enum { get; private set; }

        public DocNode? Items { get; private set; }

        public SortedDictionary<string, DocNode> Properties { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> Required { get; } = new(StringComparer.Ordinal);

        public double? Bound(string keyword) => _bounds.TryGetValue(keyword, out var value) ? value : null;

        public static SchemaView Of(DocMap schema, DocMap root)
        {
            var view = new SchemaView();
            view.Merge(schema, root, 0);
            return view;
        }

        private void Merge(DocMap schema, DocMap root, int depth)
        {
            Type ??= schema.Get("type") switch
            {
                DocScalar { Text: not null } scalar => scalar.Text,
                DocList list => string.Join("|", list.Items.OfType<DocScalar>().Select(s => s.Text ?? "null").Order(StringComparer.Ordinal)),
                _ => null,
            };
            Format ??= schema.Text("format");
            Items ??= schema.Get("items");

            if (Enum is null && schema.List("enum") is { } values)
            {
                Enum = new SortedSet<string>(values.Items.OfType<DocScalar>().Select(s => s.Text ?? "null"), StringComparer.Ordinal);
            }

            foreach (var (keyword, _) in Bounds)
            {
                if (!_bounds.ContainsKey(keyword) && schema.Get(keyword) is DocScalar { Number: { } number })
                {
                    _bounds[keyword] = number;
                }
            }

            foreach (var property in schema.Map("properties")?.Entries ?? [])
            {
                Properties.TryAdd(property.Key, property.Value);
            }

            foreach (var name in schema.List("required")?.Items.OfType<DocScalar>() ?? [])
            {
                if (name.Text is not null)
                {
                    Required.Add(name.Text);
                }
            }

            if (depth >= 16)
            {
                return;
            }

            foreach (var member in schema.List("allOf")?.Items ?? [])
            {
                if (Deref(member, root) is DocMap part && !ReferenceEquals(part, schema))
                {
                    Merge(part, root, depth + 1);
                }
            }
        }
    }
}
