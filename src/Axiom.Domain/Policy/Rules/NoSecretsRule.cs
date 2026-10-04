using System.Buffers;
using System.Text.RegularExpressions;
using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>
/// Secret/config check over the lines a change adds. A violation never repeats the secret: its
/// message and evidence name the detector, the assigned name where there is one, and the length only.
/// </summary>
public sealed partial class NoSecretsRule : BuiltInRule
{
    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const RegexOptions LinearIgnoreCase = Linear | RegexOptions.IgnoreCase;
    private const int TimeoutMilliseconds = 5000;
    private const string GenericDetector = "generic-secret";
    private const string SecretName =
        @"([A-Za-z0-9_.\-]*(?:password|passwd|pwd|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential)[A-Za-z0-9_.\-]*)";

    private static readonly Detector[] Detectors =
    [
        new("private-key", "private key", PrivateKey(), RejectPlaceholders: false),
        new("aws-access-key-id", "AWS access key ID", AwsAccessKeyId(), RejectPlaceholders: false),
        new("aws-secret-access-key", "AWS secret access key", AwsSecretAccessKey(), RejectPlaceholders: true),
        new("gcp-api-key", "Google Cloud API key", GcpApiKey(), RejectPlaceholders: false),
        new("gcp-service-account", "Google Cloud service account key file", GcpServiceAccount(), RejectPlaceholders: false),
        new("azure-storage-key", "Azure storage account key", AzureStorageKey(), RejectPlaceholders: true),
        new("azure-client-secret", "Azure AD client secret", AzureClientSecret(), RejectPlaceholders: false),
        new("github-token", "GitHub token", GitHubToken(), RejectPlaceholders: false),
        new("slack-token", "Slack token", SlackToken(), RejectPlaceholders: false),
        new("slack-webhook", "Slack webhook URL", SlackWebhook(), RejectPlaceholders: false),
        new("connection-string-password", "connection string with a password", ConnectionStringPassword(), RejectPlaceholders: true),
        new("url-credentials", "URL with embedded credentials", UrlCredentials(), RejectPlaceholders: true),
    ];

    private static readonly string[] DetectorIds = [.. Detectors.Select(d => d.Id), GenericDetector];

    private static readonly SearchValues<char> TemplateCharacters = SearchValues.Create("{}<>()[]*");

    private static readonly string[] PlaceholderWords =
        ["example", "changeme", "change_me", "change-me", "placeholder", "your", "xxxx", "redacted", "dummy", "sample", "todo", "fake", "not-a-secret"];

    private static readonly string[] ConfigExtensions =
        [".env", ".yaml", ".yml", ".properties", ".ini", ".conf", ".cfg", ".toml", ".tfvars"];

    public override string RuleId => "no-secrets";

    public override string Version => "1.0.0";

    public override string Description =>
        "Added lines must not contain credentials: private keys, AWS/GCP/Azure/GitHub/Slack tokens, connection strings or URLs "
        + "with passwords, and high-entropy values assigned to secret-like names (password, secret, token, api key, ...). "
        + "References and placeholders (${VAR}, {{ value }}, <your-key>) are ignored. Violations never contain the secret itself.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("paths", "Optional. Globs limiting which files are scanned. Default: every changed file."),
        ("exclude", "Optional. Globs exempt from scanning (for example test fixtures with fake credentials)."),
        ("detectors", "Optional. Detectors to run. Default: all. One or more of: private-key, aws-access-key-id, aws-secret-access-key, "
            + "gcp-api-key, gcp-service-account, azure-storage-key, azure-client-secret, github-token, slack-token, slack-webhook, "
            + "connection-string-password, url-credentials, generic-secret."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var paths = parameters.Globs("paths");
        var exclude = parameters.Globs("exclude");
        var enabled = parameters.Choices("detectors", DetectorIds);

        foreach (var file in ChangeSet.Present(context, paths, exclude))
        {
            var isConfig = IsConfigFile(file.Path);
            foreach (var line in ChangeSet.NewLines(file).OrderBy(l => l.Number))
            {
                var found = false;
                foreach (var detector in Detectors)
                {
                    if (!enabled.Contains(detector.Id))
                    {
                        continue;
                    }

                    var match = detector.Pattern.Match(line.Text);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var secret = SecretOf(match);
                    if (detector.RejectPlaceholders && IsPlaceholder(secret))
                    {
                        continue;
                    }

                    found = true;
                    yield return Violation(file.Path, line.Number, detector.Id, detector.Label, null, secret.Length);
                }

                if (!found && enabled.Contains(GenericDetector) && TryFindGenericSecret(line.Text, isConfig, out var name, out var length))
                {
                    yield return Violation(file.Path, line.Number, GenericDetector, "secret assigned to a credential-like name", name, length);
                }
            }
        }
    }

    /// <summary>The captured secret when the pattern isolates one, otherwise the whole match.</summary>
    private static string SecretOf(Match match)
    {
        for (var group = 1; group < match.Groups.Count; group++)
        {
            if (match.Groups[group].Success)
            {
                return match.Groups[group].Value;
            }
        }

        return match.Value;
    }

    private static PolicyViolation Violation(string path, int line, string detector, string label, string? name, int length) =>
        new(
            $"Possible {label} added in '{path}'. Remove it, rotate the credential, and load it from a secret store instead.",
            path,
            line,
            name is null ? $"{detector}: [REDACTED {length} chars]" : $"{detector}: {name} = [REDACTED {length} chars]");

    private static bool TryFindGenericSecret(string text, bool isConfig, out string name, out int length)
    {
        var quoted = QuotedAssignment().Match(text);
        if (quoted.Success)
        {
            var value = quoted.Groups[2].Success ? quoted.Groups[2].Value : quoted.Groups[3].Value;
            if (!IsPlaceholder(value) && !value.Contains(' ', StringComparison.Ordinal) && Entropy(value) >= 3.0)
            {
                name = quoted.Groups[1].Value;
                length = value.Length;
                return true;
            }
        }

        if (isConfig)
        {
            var bare = BareAssignment().Match(text);
            if (bare.Success)
            {
                var value = bare.Groups[2].Value;
                if (!IsPlaceholder(value) && value.Any(char.IsAsciiDigit) && value.Any(char.IsAsciiLetter) && Entropy(value) >= 3.5)
                {
                    name = bare.Groups[1].Value;
                    length = value.Length;
                    return true;
                }
            }
        }

        name = string.Empty;
        length = 0;
        return false;
    }

    private static bool IsConfigFile(string path)
    {
        var name = ChangeSet.FileName(path);
        return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || ConfigExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for values that reference a secret or stand in for one instead of being one.</summary>
    private static bool IsPlaceholder(string value)
    {
        if (value.Length == 0
            || value[0] is '$' or '%' or '@' or '#'
            || value.AsSpan().IndexOfAny(TemplateCharacters) >= 0
            || value.Distinct().Count() <= 2)
        {
            return true;
        }

        return PlaceholderWords.Any(w => value.Contains(w, StringComparison.OrdinalIgnoreCase))
            || value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vault:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("secretref:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shannon entropy in bits per character.</summary>
    private static double Entropy(string value)
    {
        var counts = new SortedDictionary<char, int>();
        foreach (var c in value)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----", Linear, TimeoutMilliseconds)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b", Linear, TimeoutMilliseconds)]
    private static partial Regex AwsAccessKeyId();

    [GeneratedRegex(@"aws.{0,20}(?:secret|sk).{0,20}[""'=:\s]([A-Za-z0-9/+]{40})\b", LinearIgnoreCase, TimeoutMilliseconds)]
    private static partial Regex AwsSecretAccessKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b", Linear, TimeoutMilliseconds)]
    private static partial Regex GcpApiKey();

    [GeneratedRegex(@"""type""\s*:\s*""service_account""", Linear, TimeoutMilliseconds)]
    private static partial Regex GcpServiceAccount();

    [GeneratedRegex(@"AccountKey\s*=\s*([A-Za-z0-9+/]{40,}={0,2})", LinearIgnoreCase, TimeoutMilliseconds)]
    private static partial Regex AzureStorageKey();

    [GeneratedRegex(@"\b[A-Za-z0-9_~.\-]{3}8Q~[A-Za-z0-9_~.\-]{31,34}", Linear, TimeoutMilliseconds)]
    private static partial Regex AzureClientSecret();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})", Linear, TimeoutMilliseconds)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", Linear, TimeoutMilliseconds)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"https://hooks\.slack\.com/services/T[A-Za-z0-9_]+/B[A-Za-z0-9_]+/[A-Za-z0-9_]+", Linear, TimeoutMilliseconds)]
    private static partial Regex SlackWebhook();

    [GeneratedRegex(
        @"\b(?:server|host|data source|user id|uid|username|user|database|initial catalog|endpoint)\s*=.*\b(?:password|pwd)\s*=\s*([^;""'\s]{3,})"
        + @"|\b(?:password|pwd)\s*=\s*([^;""'\s]{3,})\s*;.*\b(?:server|host|data source|user id|uid|username|user|database|initial catalog)\s*=",
        LinearIgnoreCase,
        TimeoutMilliseconds)]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^/\s:@""']+:([^@\s/""']{3,})@", Linear, TimeoutMilliseconds)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(SecretName + @"[""']?\s*(?::=|=>|=|:)\s*(?:""([^""]{8,})""|'([^']{8,})')", LinearIgnoreCase, TimeoutMilliseconds)]
    private static partial Regex QuotedAssignment();

    [GeneratedRegex(SecretName + @"[""']?\s*[=:]\s*([^\s""',;]{12,})\s*$", LinearIgnoreCase, TimeoutMilliseconds)]
    private static partial Regex BareAssignment();

    private sealed record Detector(string Id, string Label, Regex Pattern, bool RejectPlaceholders);
}
