using System.Collections.Immutable;
using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>
/// Schema/versioning check: versioned artifacts (database migrations, published schemas, event
/// contracts) are immutable once added, and every new one carries a higher version identifier.
/// </summary>
public sealed class SchemaVersioningRule : BuiltInRule
{
    public override string RuleId => "schema-versioning";

    public override string Version => "1.0.0";

    public override string Description =>
        "Files matching 'paths' must not be modified, renamed or deleted once added; a change is made by adding a new file "
        + "whose version identifier (the first number sequence in the file name, e.g. V7__x.sql, 20261003_Init.cs, order.v2.json) "
        + "is greater than every existing version in its sequence.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("paths", "Required. Globs of versioned files."),
        ("sequence", "Optional. What forms one version sequence: 'directory' (default; all matching files in a directory, "
            + "as for migrations) or 'name' (files with the same name once the version is removed, as for order.v1.json / order.v2.json)."),
        ("allowDelete", "Optional. 'true' permits deleting versioned files. Default: false."),
        ("requireVersion", "Optional. 'false' accepts added files without a version identifier. Default: true."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var paths = parameters.RequiredGlobs("paths");
        var byName = parameters.Choice("sequence", "directory", "directory", "name") == "name";
        var allowDelete = parameters.Flag("allowDelete", false);
        var requireVersion = parameters.Flag("requireVersion", true);

        var introduced = new List<ChangedFile>();
        var introducedPaths = new HashSet<string>(StringComparer.Ordinal);
        var previouslyPresent = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var change in context.Changes)
        {
            switch (change.Kind)
            {
                case ChangeKind.Modified when paths.MatchesAny(change.Path):
                    yield return new PolicyViolation(
                        $"'{change.Path}' is a versioned file and must not be modified in place; add a new version instead.",
                        change.Path,
                        change.AddedLines.IsDefaultOrEmpty ? null : change.AddedLines.Min(l => l.Number));
                    break;

                case ChangeKind.Deleted when paths.MatchesAny(change.Path):
                    previouslyPresent.Add(change.Path);
                    if (!allowDelete)
                    {
                        yield return new PolicyViolation($"'{change.Path}' is a versioned file and must not be deleted.", change.Path);
                    }

                    break;

                case ChangeKind.Renamed:
                    if (change.PreviousPath is not null && paths.MatchesAny(change.PreviousPath))
                    {
                        previouslyPresent.Add(change.PreviousPath);
                        yield return new PolicyViolation(
                            $"'{change.PreviousPath}' is a versioned file and must not be renamed (to '{change.Path}').",
                            change.Path,
                            Evidence: $"renamed from {change.PreviousPath}");
                    }
                    else if (paths.MatchesAny(change.Path))
                    {
                        introduced.Add(change);
                    }

                    introducedPaths.Add(change.Path);
                    break;

                case ChangeKind.Added:
                    introducedPaths.Add(change.Path);
                    if (paths.MatchesAny(change.Path))
                    {
                        introduced.Add(change);
                    }

                    break;
            }
        }

        if (introduced.Count == 0)
        {
            yield break;
        }

        foreach (var path in context.Tree.Paths)
        {
            if (!introducedPaths.Contains(path) && paths.MatchesAny(path))
            {
                previouslyPresent.Add(path);
            }
        }

        var existing = previouslyPresent
            .Select(path => (Path: path, Id: VersionId.From(path)))
            .Where(e => e.Id is not null)
            .ToLookup(e => e.Id!.Sequence(byName), e => (e.Path, Id: e.Id!), StringComparer.Ordinal);

        foreach (var file in introduced)
        {
            var id = VersionId.From(file.Path);
            if (id is null)
            {
                if (requireVersion)
                {
                    yield return new PolicyViolation(
                        $"'{file.Path}' is a versioned file but its name carries no version identifier.", file.Path);
                }

                continue;
            }

            var highest = existing[id.Sequence(byName)]
                .OrderByDescending(e => e.Id, VersionId.Order)
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .FirstOrDefault();

            if (highest.Id is not null && VersionId.Order.Compare(id, highest.Id) <= 0)
            {
                yield return new PolicyViolation(
                    $"'{file.Path}' has version {id.Display}, which is not greater than the existing version {highest.Id.Display}.",
                    file.Path,
                    Evidence: $"highest existing version: {highest.Path}");
            }
        }
    }

    /// <summary>The first dotted/underscored number sequence of a file name, e.g. <c>1.2</c> in <c>V1_2__add.sql</c>.</summary>
    private sealed class VersionId
    {
        private VersionId(string directory, string nameWithoutVersion, ImmutableArray<string> parts)
        {
            Directory = directory;
            NameWithoutVersion = nameWithoutVersion;
            Parts = parts;
        }

        public static IComparer<VersionId> Order { get; } = Comparer<VersionId>.Create(Compare);

        public string Directory { get; }

        public string NameWithoutVersion { get; }

        public ImmutableArray<string> Parts { get; }

        public string Display => string.Join('.', Parts);

        public string Sequence(bool byName) => byName ? Directory + "/" + NameWithoutVersion : Directory;

        public static VersionId? From(string path)
        {
            var name = ChangeSet.FileName(path);
            var start = 0;
            while (start < name.Length && !char.IsAsciiDigit(name[start]))
            {
                start++;
            }

            if (start == name.Length)
            {
                return null;
            }

            var parts = ImmutableArray.CreateBuilder<string>();
            var i = start;
            while (true)
            {
                var partStart = i;
                while (i < name.Length && char.IsAsciiDigit(name[i]))
                {
                    i++;
                }

                var digits = name[partStart..i].TrimStart('0');
                parts.Add(digits.Length == 0 ? "0" : digits);
                if (i + 1 < name.Length && name[i] is '.' or '_' && char.IsAsciiDigit(name[i + 1]))
                {
                    i++;
                    continue;
                }

                break;
            }

            return new VersionId(ChangeSet.Directory(path), name[..start] + name[i..], parts.ToImmutable());
        }

        private static int Compare(VersionId? left, VersionId? right)
        {
            if (left is null || right is null)
            {
                return (left is null ? 0 : 1) - (right is null ? 0 : 1);
            }

            for (var i = 0; i < Math.Min(left.Parts.Length, right.Parts.Length); i++)
            {
                var a = left.Parts[i];
                var b = right.Parts[i];
                var order = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (order != 0)
                {
                    return order;
                }
            }

            return left.Parts.Length.CompareTo(right.Parts.Length);
        }
    }
}
