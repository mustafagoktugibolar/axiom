using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Forbidden packages: dependency IDs (optionally limited to a version range) that must not be declared.</summary>
public sealed class ForbiddenPackageRule : BuiltInRule
{
    public override string RuleId => "forbidden-package";

    public override string Version => "1.0.0";

    public override string Description =>
        "Changed package manifests must not declare forbidden packages. Reads NuGet (*.csproj, *.fsproj, *.vbproj, "
        + "Directory.Packages.props, Directory.Build.props), npm (package.json), Python (requirements*.txt, constraints*.txt, "
        + "pyproject.toml), Go (go.mod) and Maven/Gradle (pom.xml, build.gradle, build.gradle.kts). Added and renamed manifests "
        + "are evaluated in full, modified manifests on their added lines. With a version range, the declared version (the lower "
        + "bound of a range such as ^1.2.3) is compared; a version that cannot be read is reported rather than assumed safe. "
        + "A NuGet reference without a version is left to the central package file that carries it.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("packages", "Required. Forbidden package IDs, optionally 'id@range'. IDs are case-insensitive and may use '*'. "
            + "Maven IDs are 'group:artifact'. A range is space-separated comparators that must all hold, "
            + "e.g. Newtonsoft.Json@<13.0.1, lodash@>=4.0.0 <4.17.21, log4j:log4j."),
        ("paths", "Optional. Globs limiting which manifests are checked. Default: every changed manifest."),
        ("exclude", "Optional. Globs exempt from the rule."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var forbidden = parameters.RequiredList("packages").Select(entry => ParseEntry(parameters, entry)).ToArray();
        var paths = parameters.Globs("paths");
        var exclude = parameters.Globs("exclude");

        foreach (var file in ChangeSet.Present(context, paths, exclude))
        {
            if (file.Content is null || !PackageManifests.IsManifest(file.Path))
            {
                continue;
            }

            IReadOnlyList<PackageDeclaration> declarations;
            string? problem = null;
            try
            {
                declarations = PackageManifests.Read(file.Path, file.Content);
            }
            catch (FormatException ex)
            {
                declarations = [];
                problem = ex.Message;
            }

            if (problem is not null)
            {
                yield return new PolicyViolation(
                    $"'{file.Path}' could not be parsed, so its dependencies cannot be verified.", file.Path, Evidence: problem);
                continue;
            }

            var introduced = ChangeSet.IntroducedLines(file);
            foreach (var declaration in declarations.Where(d => introduced(d.Line)))
            {
                foreach (var (pattern, range) in forbidden)
                {
                    if (!IdMatches(pattern, declaration))
                    {
                        continue;
                    }

                    var violation = Judge(file.Path, declaration, range);
                    if (violation is not null)
                    {
                        yield return violation;
                        break;
                    }
                }
            }
        }
    }

    private static (string Pattern, PackageVersionRange? Range) ParseEntry(RuleParameters parameters, string entry)
    {
        var at = entry.LastIndexOf('@');
        if (at <= 0)
        {
            return (entry, null);
        }

        var id = entry[..at].Trim();
        var rangeText = entry[(at + 1)..].Trim();
        return id.Length > 0 && PackageVersionRange.TryParse(rangeText, out var range)
            ? (id, range)
            : throw parameters.Malformed("packages", $"'{entry}' is not 'id' or 'id@range' (range example: >=1.0.0 <2.0.0)");
    }

    private static bool IdMatches(string pattern, PackageDeclaration declaration) =>
        Wildcard.IsMatch(pattern, declaration.Id, ignoreCase: true)
        || (declaration.Ecosystem == PackageManifests.PyPi
            && Wildcard.IsMatch(PackageManifests.NormalizePythonName(pattern), PackageManifests.NormalizePythonName(declaration.Id)));

    private static PolicyViolation? Judge(string path, PackageDeclaration declaration, PackageVersionRange? range)
    {
        var shown = declaration.Version is null ? declaration.Id : $"{declaration.Id} {declaration.Version}";
        if (range is null)
        {
            return new PolicyViolation(
                $"'{path}' declares the forbidden package '{declaration.Id}'.", path, declaration.Line, $"{declaration.Ecosystem}: {shown}");
        }

        if (declaration.Version is null)
        {
            return declaration.Ecosystem == PackageManifests.NuGet
                ? null
                : new PolicyViolation(
                    $"'{path}' declares '{declaration.Id}' without a version, so the forbidden range {range.Text} cannot be ruled out.",
                    path,
                    declaration.Line,
                    $"{declaration.Ecosystem}: {shown}");
        }

        if (!PackageVersion.TryParse(declaration.Version, out var version))
        {
            return new PolicyViolation(
                $"'{path}' declares '{declaration.Id}' with version '{declaration.Version}', which cannot be compared against the forbidden range {range.Text}.",
                path,
                declaration.Line,
                $"{declaration.Ecosystem}: {shown}");
        }

        return range.Contains(version)
            ? new PolicyViolation(
                $"'{path}' declares '{declaration.Id}' {declaration.Version}, which is in the forbidden range {range.Text}.",
                path,
                declaration.Line,
                $"{declaration.Ecosystem}: {shown}")
            : null;
    }
}
