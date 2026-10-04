using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Deployment security checks for Kubernetes manifests, Helm values and Dockerfiles.</summary>
public sealed class DeploymentSecurityRule : BuiltInRule
{
    private const string Privileged = "privileged";
    private const string RunAsNonRoot = "run-as-non-root";
    private const string HostNamespaces = "host-namespaces";
    private const string LatestTag = "latest-tag";
    private const string ReadOnlyRoot = "read-only-root-filesystem";
    private const string ResourceLimits = "resource-limits";
    private const string DockerfileUser = "dockerfile-user";

    private static readonly string[] Checks =
        [Privileged, RunAsNonRoot, HostNamespaces, LatestTag, ReadOnlyRoot, ResourceLimits, DockerfileUser];

    public override string RuleId => "deployment-security";

    public override string Version => "1.0.0";

    public override string Description =>
        "Checks changed deployment files in full. YAML (Kubernetes manifests, Helm values): no 'privileged: true'; no hostNetwork/"
        + "hostPID/hostIPC; no 'runAsNonRoot: false' or 'runAsUser: 0'; no 'readOnlyRootFilesystem: false'; no ':latest' image or "
        + "'tag: latest'. Every container of a pod spec must additionally set runAsNonRoot (container or pod level), "
        + "readOnlyRootFilesystem: true, resources.limits, and an image with a tag or digest. Dockerfiles: base images must be "
        + "pinned to a tag other than latest, and the final stage must switch to a non-root USER. Helm templates are read as "
        + "written; templated values are accepted, and a file that cannot be parsed is reported.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("paths", "Optional. Globs of deployment files. Default: every changed *.yaml/*.yml file and Dockerfile. "
            + "JSON manifests are only checked when selected here."),
        ("exclude", "Optional. Globs exempt from the rule."),
        ("checks", "Optional. Checks to run. Default: all. One or more of: privileged, run-as-non-root, host-namespaces, "
            + "latest-tag, read-only-root-filesystem, resource-limits, dockerfile-user."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var paths = parameters.Globs("paths");
        var exclude = parameters.Globs("exclude");
        var checks = parameters.Choices("checks", Checks).ToHashSet(StringComparer.Ordinal);
        var violations = new List<PolicyViolation>();

        foreach (var file in ChangeSet.Present(context, paths, exclude))
        {
            if (file.Content is null)
            {
                continue;
            }

            var report = new Reporter(file.Path, checks, violations);
            if (IsDockerfile(file.Path))
            {
                CheckDockerfile(file.Content, report);
            }
            else if (StructuredDocument.IsYamlPath(file.Path) || (!paths.IsEmpty && StructuredDocument.IsJsonPath(file.Path)))
            {
                try
                {
                    foreach (var document in StructuredDocument.Parse(file.Path, file.Content))
                    {
                        Walk(document, report, 0);
                    }
                }
                catch (FormatException ex)
                {
                    violations.Add(new PolicyViolation(
                        $"'{file.Path}' could not be parsed, so its deployment security cannot be verified.", file.Path, Evidence: ex.Message));
                }
            }
        }

        return violations;
    }

    private static bool IsDockerfile(string path)
    {
        var name = ChangeSet.FileName(path);
        return name is "Dockerfile" or "Containerfile"
            || name.StartsWith("Dockerfile.", StringComparison.Ordinal)
            || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static void Walk(DocNode node, Reporter report, int depth)
    {
        if (depth > PolicyLimits.MaxNestingDepth)
        {
            return;
        }

        if (node is DocList list)
        {
            foreach (var item in list.Items)
            {
                Walk(item, report, depth + 1);
            }

            return;
        }

        if (node is not DocMap map)
        {
            return;
        }

        foreach (var entry in map.Entries)
        {
            if (entry.Value is DocScalar scalar)
            {
                CheckSetting(entry, scalar, report);
            }

            Walk(entry.Value, report, depth + 1);
        }

        if (map.List("containers") is not null)
        {
            CheckPodSpec(map, report);
        }
    }

    private static void CheckSetting(DocEntry entry, DocScalar value, Reporter report)
    {
        switch (entry.Key)
        {
            case "privileged" when value.IsTrue:
                report.Add(Privileged, "Privileged containers are not allowed.", entry.Line, "privileged: true");
                break;
            case "hostNetwork" or "hostPID" or "hostIPC" when value.IsTrue:
                report.Add(HostNamespaces, $"Sharing the host namespace ({entry.Key}) is not allowed.", entry.Line, $"{entry.Key}: true");
                break;
            case "runAsNonRoot" when value.IsFalse:
                report.Add(RunAsNonRoot, "Containers must not be allowed to run as root.", entry.Line, "runAsNonRoot: false");
                break;
            case "runAsUser" when value.Number == 0:
                report.Add(RunAsNonRoot, "Containers must not run as UID 0.", entry.Line, "runAsUser: 0");
                break;
            case "readOnlyRootFilesystem" when value.IsFalse:
                report.Add(ReadOnlyRoot, "The root filesystem must be read-only.", entry.Line, "readOnlyRootFilesystem: false");
                break;
            case "tag" when string.Equals(value.Text, "latest", StringComparison.OrdinalIgnoreCase):
                report.Add(LatestTag, "Image tag 'latest' is not allowed; pin a version or digest.", entry.Line, "tag: latest");
                break;
            case "image" when value.Text is { } image && ImageTag(image) == "latest":
                report.Add(LatestTag, "Image tag 'latest' is not allowed; pin a version or digest.", entry.Line, $"image: {image}");
                break;
        }
    }

    private static void CheckPodSpec(DocMap podSpec, Reporter report)
    {
        var podSecurity = podSpec.Map("securityContext");
        foreach (var listName in new[] { "containers", "initContainers" })
        {
            foreach (var container in podSpec.List(listName)?.Items.OfType<DocMap>() ?? [])
            {
                var name = container.Text("name") ?? "(unnamed)";
                var security = container.Map("securityContext");

                if (security?.Get("runAsNonRoot") is null && podSecurity?.Get("runAsNonRoot") is null)
                {
                    report.Add(RunAsNonRoot, $"Container '{name}' must set securityContext.runAsNonRoot: true.", container.Line, "runAsNonRoot is not set");
                }

                if (security?.Get("readOnlyRootFilesystem") is null)
                {
                    report.Add(
                        ReadOnlyRoot,
                        $"Container '{name}' must set securityContext.readOnlyRootFilesystem: true.",
                        container.Line,
                        "readOnlyRootFilesystem is not set");
                }

                var resources = container.Get("resources");
                var templated = resources is DocScalar { Text: { } text } && text.Contains("{{", StringComparison.Ordinal);
                if (!templated && !StructuredDocument.HasValue((resources as DocMap)?.Get("limits")))
                {
                    report.Add(ResourceLimits, $"Container '{name}' must declare resources.limits.", container.Line, "resources.limits is not set");
                }

                if (container.Text("image") is { } image && ImageTag(image) is null)
                {
                    report.Add(
                        LatestTag,
                        $"Container '{name}' uses an image without a tag, which resolves to 'latest'; pin a version or digest.",
                        container.Entry("image")!.Line,
                        $"image: {image}");
                }
            }
        }
    }

    /// <summary>
    /// The tag of an image reference; <c>null</c> when it has none. A digest or a templated reference
    /// counts as pinned and is returned as a non-<c>latest</c> marker.
    /// </summary>
    private static string? ImageTag(string image)
    {
        if (image.Contains('@', StringComparison.Ordinal) || image.Contains("{{", StringComparison.Ordinal) || image.Contains('$', StringComparison.Ordinal))
        {
            return "(pinned)";
        }

        var name = image[(image.LastIndexOf('/') + 1)..];
        var colon = name.LastIndexOf(':');
        return colon < 0 ? null : name[(colon + 1)..].Trim().ToLowerInvariant();
    }

    private static void CheckDockerfile(string content, Reporter report)
    {
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? lastFromLine = null;
        (int Line, string User)? finalUser = null;

        foreach (var (line, instruction, arguments) in DockerInstructions(content))
        {
            if (instruction == "FROM")
            {
                lastFromLine = line;
                finalUser = null;
                var tokens = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => !t.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();
                if (tokens.Length == 0)
                {
                    continue;
                }

                var image = tokens[0];
                if (!string.Equals(image, "scratch", StringComparison.OrdinalIgnoreCase) && !stages.Contains(image))
                {
                    var tag = ImageTag(image);
                    if (tag is null or "latest")
                    {
                        report.Add(
                            LatestTag,
                            tag is null
                                ? $"Base image '{image}' has no tag, which resolves to 'latest'; pin a version or digest."
                                : "Base image tag 'latest' is not allowed; pin a version or digest.",
                            line,
                            $"FROM {image}");
                    }
                }

                if (tokens.Length >= 3 && string.Equals(tokens[1], "AS", StringComparison.OrdinalIgnoreCase))
                {
                    stages.Add(tokens[2]);
                }
            }
            else if (instruction == "USER")
            {
                finalUser = (line, arguments.Trim());
            }
        }

        if (lastFromLine is null)
        {
            return;
        }

        if (finalUser is null)
        {
            report.Add(DockerfileUser, "The final stage never switches to a non-root USER, so the container runs as root.", lastFromLine, "no USER instruction");
            return;
        }

        var user = finalUser.Value.User.Split(':')[0].Trim();
        if (user is "0" || string.Equals(user, "root", StringComparison.OrdinalIgnoreCase))
        {
            report.Add(DockerfileUser, "The final stage runs as root.", finalUser.Value.Line, $"USER {finalUser.Value.User}");
        }
    }

    /// <summary>Dockerfile instructions with backslash continuations joined; comment lines are skipped.</summary>
    private static IEnumerable<(int Line, string Instruction, string Arguments)> DockerInstructions(string content)
    {
        var lines = ChangeSet.SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            var start = i;
            var text = lines[i].Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }

            while (text.EndsWith('\\') && i + 1 < lines.Length)
            {
                i++;
                var next = lines[i].Trim();
                text = text[..^1] + " " + (next.StartsWith('#') ? string.Empty : next);
            }

            var space = text.IndexOfAny([' ', '\t']);
            yield return space < 0
                ? (start + 1, text.ToUpperInvariant(), string.Empty)
                : (start + 1, text[..space].ToUpperInvariant(), text[(space + 1)..].Trim());
        }
    }

    private sealed class Reporter(string path, HashSet<string> enabled, List<PolicyViolation> violations)
    {
        public void Add(string check, string message, int? line, string evidence)
        {
            if (enabled.Contains(check))
            {
                violations.Add(new PolicyViolation($"{message} ({path})", path, line, $"{check}: {evidence}"));
            }
        }
    }
}
