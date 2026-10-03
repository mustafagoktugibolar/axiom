using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Required test presence: source changes must be accompanied by test changes.</summary>
public sealed class RequiredTestsRule : BuiltInRule
{
    public override string RuleId => "required-tests";

    public override string Version => "1.0.0";

    public override string Description =>
        "Added, modified or renamed source files matching 'source' require an added or modified test file matching 'tests' in the same change.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("source", "Required. Globs of source files that need tests."),
        ("tests", "Required. Globs of test files."),
        ("exclude", "Optional. Globs of source files that do not need tests (generated code, DTOs)."),
        ("pairing", "Optional. 'any' (default): any changed test file satisfies every source file. "
            + "'name': each source file needs a changed test file whose name contains the source file's base name."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var source = parameters.RequiredGlobs("source");
        var tests = parameters.RequiredGlobs("tests");
        var exclude = parameters.Globs("exclude");
        var byName = parameters.Choice("pairing", "any", "any", "name") == "name";

        var changedTests = context.Changes
            .Where(c => c.Kind != ChangeKind.Deleted && tests.MatchesAny(c.Path))
            .Select(c => c.Path)
            .ToArray();

        foreach (var file in ChangeSet.Present(context, source, exclude))
        {
            if (tests.MatchesAny(file.Path))
            {
                continue;
            }

            var baseName = BaseName(file.Path);
            var satisfied = byName
                ? changedTests.Any(t => ChangeSet.FileName(t).Contains(baseName, StringComparison.OrdinalIgnoreCase))
                : changedTests.Length > 0;

            if (!satisfied)
            {
                yield return new PolicyViolation(
                    byName
                        ? $"'{file.Path}' changed without a changed test file named after '{baseName}'."
                        : $"'{file.Path}' changed without any accompanying test change.",
                    file.Path,
                    Evidence: $"expected a changed file matching: {string.Join(", ", tests.Select(t => t.Value))}");
            }
        }
    }

    private static string BaseName(string path)
    {
        var name = ChangeSet.FileName(path);
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? name[..dot] : name;
    }
}
