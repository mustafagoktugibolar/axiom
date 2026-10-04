using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Public contract compatibility for OpenAPI documents and JSON Schema files.</summary>
public sealed class ApiCompatibilityRule : BuiltInRule
{
    public override string RuleId => "api-compatibility";

    public override string Version => "1.0.0";

    public override string Description =>
        "Compares the previous and new revision of OpenAPI 2/3 documents and JSON Schema files (JSON or YAML) and reports breaking "
        + "changes: removed path, operation, response or media type; added or newly required request parameter, body or property; "
        + "changed type or format; removed enum value; narrowed request constraint; removed or no-longer-required response property. "
        + "For JSON Schema files: removed properties, newly required properties and type changes. Deleting or renaming a contract "
        + "is breaking. A contract that cannot be read or has no previous revision to compare is reported, never assumed compatible.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("paths", "Required. Globs of contract files, e.g. api/**/*.yaml or schemas/**/*.json."),
        ("kind", "Optional. 'auto' (default; detected from the document), 'openapi' or 'json-schema'."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var paths = parameters.RequiredGlobs("paths");
        var kind = parameters.Choice("kind", "auto", "auto", "openapi", "json-schema");

        foreach (var change in context.Changes)
        {
            var previousPath = change.PreviousPath ?? change.Path;
            if (change.Kind == ChangeKind.Added || !(paths.MatchesAny(previousPath) || paths.MatchesAny(change.Path)))
            {
                continue;
            }

            if (change.Kind == ChangeKind.Deleted)
            {
                yield return new PolicyViolation($"Contract '{change.Path}' was removed.", change.Path);
                continue;
            }

            if (change.Kind == ChangeKind.Renamed && paths.MatchesAny(previousPath) && !string.Equals(previousPath, change.Path, StringComparison.Ordinal))
            {
                yield return new PolicyViolation(
                    $"Contract '{previousPath}' was moved to '{change.Path}'.", change.Path, Evidence: $"renamed from {previousPath}");
            }

            if (change.PreviousContent is null || change.Content is null)
            {
                yield return new PolicyViolation(
                    $"Contract '{change.Path}' changed but its {(change.Content is null ? "new" : "previous")} content is unavailable, so compatibility cannot be verified.",
                    change.Path);
                continue;
            }

            var previous = TryParse(previousPath, change.PreviousContent, out var previousProblem);
            var current = TryParse(change.Path, change.Content, out var currentProblem);
            if (previous is null || current is null)
            {
                yield return new PolicyViolation(
                    $"Contract '{change.Path}' could not be parsed ({(current is null ? "new" : "previous")} revision), so compatibility cannot be verified.",
                    change.Path,
                    Evidence: currentProblem ?? previousProblem);
                continue;
            }

            var isOpenApi = kind == "openapi" || (kind == "auto" && ContractComparer.IsOpenApi(previous));
            var isSchema = kind == "json-schema" || (kind == "auto" && !isOpenApi && ContractComparer.IsJsonSchema(previous));
            var changes = isOpenApi
                ? ContractComparer.CompareOpenApi(previous, current)
                : isSchema ? ContractComparer.CompareJsonSchema(previous, current) : [];

            foreach (var breaking in changes)
            {
                yield return new PolicyViolation($"Breaking change in '{change.Path}': {breaking.Message}", change.Path, breaking.Line, breaking.Location);
            }
        }
    }

    private static DocMap? TryParse(string path, string content, out string? problem)
    {
        problem = null;
        try
        {
            var documents = StructuredDocument.Parse(path, content);
            if (documents.Count > 0 && documents[0] is DocMap root)
            {
                return root;
            }

            problem = "the document root is not an object";
            return null;
        }
        catch (FormatException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
