using System.Text;
using System.Text.Json;

namespace Axiom.Cli;

internal static class ExitCodes
{
    /// <summary>ALLOW or ALLOW_WITH_WARNINGS.</summary>
    public const int Allowed = 0;

    /// <summary>Invalid command line.</summary>
    public const int Usage = 2;

    /// <summary>No verdict: Axiom or the source-control system was unavailable, or the request was rejected. Fails closed.</summary>
    public const int NoVerdict = 3;

    /// <summary>REQUIRE_REVIEW: a human authority must approve.</summary>
    public const int RequireReview = 10;

    /// <summary>BLOCK, or invalid governance records.</summary>
    public const int Blocked = 20;

    public static int ForVerdict(string? verdict) => verdict switch
    {
        "ALLOW" or "ALLOW_WITH_WARNINGS" => Allowed,
        "REQUIRE_REVIEW" => RequireReview,
        "BLOCK" => Blocked,
        _ => NoVerdict,
    };
}

internal enum OutputFormat
{
    Text,
    Json,
    GitHub,
    Azure,
}

/// <summary>Renders evaluation results for people and for CI log annotations.</summary>
internal static class Output
{
    internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void WriteEvaluation(JsonElement result, OutputFormat format, TextWriter stdout)
    {
        switch (format)
        {
            case OutputFormat.Json:
                stdout.WriteLine(JsonSerializer.Serialize(result, Pretty));
                return;
            case OutputFormat.GitHub:
                WriteSummary(result, stdout);
                foreach (var finding in Findings(result))
                {
                    stdout.WriteLine(GitHubCommand(finding));
                }

                return;
            case OutputFormat.Azure:
                WriteSummary(result, stdout);
                foreach (var finding in Findings(result))
                {
                    stdout.WriteLine(AzureCommand(finding));
                }

                return;
            default:
                WriteSummary(result, stdout);
                foreach (var finding in Findings(result))
                {
                    stdout.WriteLine(FindingLine(finding));
                }

                foreach (var action in Strings(result, "requiredActions"))
                {
                    stdout.WriteLine($"  next: {action}");
                }

                return;
        }
    }

    public static void WriteError(ApiResponse response, TextWriter stderr)
    {
        if (response.TransportError is not null)
        {
            stderr.WriteLine($"axiom: could not reach Axiom ({response.TransportError}). No governance verdict was produced.");
            return;
        }

        var code = response.ErrorCode ?? $"HTTP_{response.Status}";
        stderr.WriteLine($"axiom: {code}: {response.ErrorMessage ?? "The request failed."} No governance verdict was produced.");
    }

    private static void WriteSummary(JsonElement result, TextWriter stdout)
    {
        stdout.WriteLine($"{Text(result, "verdict")}  {Text(result, "evaluationId")}  stage={Text(result, "stage")}  snapshot={Text(result, "snapshotId")}  receipt={Text(result, "receiptId")}");
    }

    private static IEnumerable<JsonElement> Findings(JsonElement result) =>
        result.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array
            ? findings.EnumerateArray().Where(f => !f.TryGetProperty("waivedBy", out var w) || w.ValueKind == JsonValueKind.Null)
            : [];

    private static string FindingLine(JsonElement finding)
    {
        var where = Location(finding);
        var ids = Strings(finding, "governanceIds");
        return $"  [{Text(finding, "severity")}] {Text(finding, "code")}{(ids.Length > 0 ? $" ({string.Join(", ", ids)})" : string.Empty)}{(where.Length > 0 ? $" {where}" : string.Empty)}: {Text(finding, "message")}";
    }

    internal static string GitHubCommand(JsonElement finding)
    {
        var level = Severity(finding) switch { "BLOCK" or "REQUIRE_REVIEW" => "error", "WARN" => "warning", _ => "notice" };
        var properties = new StringBuilder();
        if (Text(finding, "path") is { Length: > 0 } path)
        {
            properties.Append("file=").Append(EscapeProperty(path));
            if (finding.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number)
            {
                properties.Append(",line=").Append(line.GetInt32());
            }

            properties.Append(',');
        }

        properties.Append("title=").Append(EscapeProperty(Text(finding, "code")));
        return $"::{level} {properties}::{EscapeData(Text(finding, "message"))}";
    }

    internal static string AzureCommand(JsonElement finding)
    {
        var type = Severity(finding) is "BLOCK" or "REQUIRE_REVIEW" ? "error" : "warning";
        var properties = new StringBuilder($"type={type};code={EscapeAzure(Text(finding, "code"))}");
        if (Text(finding, "path") is { Length: > 0 } path)
        {
            properties.Append(";sourcepath=").Append(EscapeAzure(path));
            if (finding.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number)
            {
                properties.Append(";linenumber=").Append(line.GetInt32());
            }
        }

        return $"##vso[task.logissue {properties}]{EscapeAzure(Text(finding, "message"))}";
    }

    private static string Severity(JsonElement finding) => Text(finding, "severity");

    private static string Location(JsonElement finding)
    {
        var path = Text(finding, "path");
        return path.Length == 0 ? string.Empty : finding.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number ? $"{path}:{line.GetInt32()}" : path;
    }

    internal static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

    // GitHub workflow commands: https://docs.github.com/actions/reference/workflow-commands-for-github-actions
    private static string EscapeData(string value) => value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static string EscapeProperty(string value) =>
        EscapeData(value).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);

    private static string EscapeAzure(string value) =>
        value.Replace("%", "%AZP25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal);
}
