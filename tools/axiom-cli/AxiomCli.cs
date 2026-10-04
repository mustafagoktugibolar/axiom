using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Governance.Parsing;

namespace Axiom.Cli;

/// <summary>
/// The CI and local command line (task 8.8). Evaluation commands call the Axiom API, so the verdict is
/// always the server's, computed on immutable commits independent of this machine. Exit codes are the
/// contract with CI: see <see cref="ExitCodes"/> and docs/operations/branch-policy.md.
/// </summary>
public static class AxiomCli
{
    public const string UrlVariable = "AXIOM_URL";
    public const string TokenVariable = "AXIOM_TOKEN";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(environment);

        var context = new CliContext(stdout, stderr, environment, handler);
        var root = BuildCommands(context);
        var configuration = new InvocationConfiguration { Output = stdout, Error = stderr, EnableDefaultExceptionHandler = false };
        try
        {
            var exit = await root.Parse(args).InvokeAsync(configuration, cancellationToken);
            return exit == 1 ? ExitCodes.Usage : exit;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("axiom: cancelled. No governance verdict was produced.");
            return ExitCodes.NoVerdict;
        }
    }

    private static RootCommand BuildCommands(CliContext context)
    {
        var format = new Option<OutputFormat>("--format") { Description = "text (default), json, github (workflow annotations) or azure (pipeline logging commands).", DefaultValueFactory = _ => OutputFormat.Text };
        var url = new Option<string?>("--url") { Description = $"Axiom base URL. Defaults to ${UrlVariable}. Must be https (http only for localhost)." };
        var repository = new Option<string>("--repository") { Description = "Repository name, namespaced name or reference.", Required = true };
        var baseSha = new Option<string>("--base") { Description = "Base commit SHA.", Required = true };
        var head = new Option<string>("--head") { Description = "Head commit SHA (the merge candidate).", Required = true };
        var design = new Option<string?>("--design") { Description = "Design evaluation ID the change implements (required for significant changes)." };
        var reference = new Option<string?>("--ref") { Description = "Branch or ref name." };
        var pullRequest = new Option<string>("--pull-request") { Description = "Pull request number or ID.", Required = true };

        var evaluatePr = new Command("evaluate-pr", "Merge gate: re-evaluate a pull request on its immutable commit SHAs.")
            { repository, baseSha, head, pullRequest, design, reference, url, format };
        evaluatePr.SetAction((result, token) => context.EvaluateAsync(
            "/v1/evaluations/pr",
            new { repository = result.GetValue(repository), baseSha = result.GetValue(baseSha), headSha = result.GetValue(head), pullRequest = result.GetValue(pullRequest), designEvaluationId = result.GetValue(design), @ref = result.GetValue(reference) },
            result.GetValue(url), result.GetValue(format), token));

        var diffPullRequest = new Option<string?>("--pull-request") { Description = "Pull request number or ID; makes this a pull-request evaluation." };
        var evaluateDiff = new Command("evaluate-diff", "Validate the actual change between two commits.")
            { repository, baseSha, head, design, reference, diffPullRequest, url, format };
        evaluateDiff.SetAction((result, token) => context.EvaluateAsync(
            "/v1/evaluations/diff",
            new { repository = result.GetValue(repository), baseSha = result.GetValue(baseSha), headSha = result.GetValue(head), pullRequest = result.GetValue(diffPullRequest), designEvaluationId = result.GetValue(design), @ref = result.GetValue(reference) },
            result.GetValue(url), result.GetValue(format), token));

        var refRequired = new Option<string>("--ref") { Description = "Branch or ref name.", Required = true };
        var task = new Option<string>("--task") { Description = "What the change is meant to do.", Required = true };
        var paths = new Option<string[]>("--path") { Description = "Expected changed path or glob (repeatable).", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var preflight = new Command("preflight", "Before editing: resolve scope and applicable governance for a task.")
            { repository, refRequired, task, paths, url, format };
        preflight.SetAction((result, token) => context.EvaluateAsync(
            "/v1/evaluations/preflight",
            new { repository = result.GetValue(repository), @ref = result.GetValue(refRequired), task = result.GetValue(task), paths = result.GetValue(paths) is { Length: > 0 } p ? p : null },
            result.GetValue(url), result.GetValue(format), token));

        var receiptId = new Option<string?>("--id") { Description = "Receipt or evaluation ID." };
        var commit = new Option<string?>("--commit") { Description = "Commit SHA, with --repository." };
        var receiptRepository = new Option<string?>("--repository") { Description = "Repository, with --commit." };
        var receipt = new Command("receipt", "Fetch the immutable receipt of an evaluation (requires the audit-read right).")
            { receiptId, receiptRepository, commit, url };
        receipt.SetAction((result, token) => context.ReceiptAsync(result.GetValue(receiptId), result.GetValue(receiptRepository), result.GetValue(commit), result.GetValue(url), token));

        var root = new Option<DirectoryInfo>("--root") { Description = "Governance root directory (contains decisions/, standards/, ...).", Required = true };
        var validate = new Command("validate-governance", "Validate governance records locally: schemas, references, lifecycle, supersession. Needs no server.")
            { root };
        validate.SetAction(result => context.ValidateGovernance(result.GetValue(root)!));

        return new RootCommand("Axiom engineering governance CLI") { evaluatePr, evaluateDiff, preflight, receipt, validate };
    }

    private sealed class CliContext(TextWriter stdout, TextWriter stderr, Func<string, string?> environment, HttpMessageHandler? handler)
    {
        public async Task<int> EvaluateAsync(string path, object body, string? url, OutputFormat format, CancellationToken cancellationToken)
        {
            using var client = Connect(url);
            if (client is null)
            {
                return ExitCodes.NoVerdict;
            }

            var response = await client.PostAsync(path, body, cancellationToken);
            if (!response.IsSuccess)
            {
                Output.WriteError(response, stderr);
                return ExitCodes.NoVerdict;
            }

            Output.WriteEvaluation(response.Body!.Value, format, stdout);
            return ExitCodes.ForVerdict(Output.Text(response.Body.Value, "verdict"));
        }

        public async Task<int> ReceiptAsync(string? id, string? repository, string? commit, string? url, CancellationToken cancellationToken)
        {
            string path;
            if (!string.IsNullOrWhiteSpace(id))
            {
                path = $"/v1/receipts/{Uri.EscapeDataString(id)}";
            }
            else if (!string.IsNullOrWhiteSpace(repository) && !string.IsNullOrWhiteSpace(commit))
            {
                path = $"/v1/receipts?repository={Uri.EscapeDataString(repository)}&commitSha={Uri.EscapeDataString(commit)}";
            }
            else
            {
                stderr.WriteLine("axiom: provide --id, or both --repository and --commit.");
                return ExitCodes.Usage;
            }

            using var client = Connect(url);
            if (client is null)
            {
                return ExitCodes.NoVerdict;
            }

            var response = await client.GetAsync(path, cancellationToken);
            if (!response.IsSuccess)
            {
                Output.WriteError(response, stderr);
                return ExitCodes.NoVerdict;
            }

            stdout.WriteLine(JsonSerializer.Serialize(response.Body, Output.Pretty));
            var verified = response.Body!.Value.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
            if (!verified)
            {
                stderr.WriteLine("axiom: the receipt's digest does not verify.");
            }

            return verified ? ExitCodes.ForVerdict(Output.Text(response.Body.Value, "verdict")) : ExitCodes.Blocked;
        }

        public int ValidateGovernance(DirectoryInfo root)
        {
            if (!root.Exists)
            {
                stderr.WriteLine($"axiom: directory '{root.FullName}' does not exist.");
                return ExitCodes.Usage;
            }

            var parser = new GovernanceRecordParser();
            var records = new List<GovernanceRecord>();
            var issues = new List<ValidationIssue>();
            var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => (File: f, Relative: Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/')))
                .Where(f => !f.Relative.Split('/').Any(s => s.StartsWith('.')))
                .OrderBy(f => f.Relative, StringComparer.Ordinal)
                .ToList();

            var examined = 0;
            foreach (var (file, relative) in files.Where(f => parser.IsRecordPath(f.Relative)))
            {
                examined++;
                var parsed = parser.Parse(relative, File.ReadAllText(file.FullName));
                issues.AddRange(parsed.Issues);
                if (parsed.Record is not null && parsed.Issues.All(i => i.Severity != IssueSeverity.Error))
                {
                    records.Add(parsed.Record);
                }
            }

            issues.AddRange(GovernanceSetValidator.Validate(records));
            foreach (var issue in issues.OrderBy(i => i.SourcePath, StringComparer.Ordinal).ThenBy(i => i.RecordId, StringComparer.Ordinal).ThenBy(i => i.Code, StringComparer.Ordinal))
            {
                var where = issue.SourcePath ?? issue.RecordId ?? "-";
                (issue.Severity == IssueSeverity.Error ? stderr : stdout).WriteLine($"{issue.Severity.ToString().ToUpperInvariant()} {issue.Code} {where}: {issue.Message}");
            }

            var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
            stdout.WriteLine($"{examined} record file(s) examined, {records.Count} valid, {errors} error(s), {issues.Count - errors} warning(s).");
            return errors == 0 ? ExitCodes.Allowed : ExitCodes.Blocked;
        }

        private AxiomClient? Connect(string? urlOption)
        {
            var text = urlOption ?? environment(UrlVariable);
            if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url))
            {
                stderr.WriteLine($"axiom: set --url or ${UrlVariable} to the Axiom base URL.");
                return null;
            }

            // The token is a bearer credential: it only goes over https, or to a loopback address for local development.
            if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            {
                stderr.WriteLine("axiom: the Axiom URL must use https (http is accepted only for localhost).");
                return null;
            }

            var token = environment(TokenVariable);
            if (string.IsNullOrWhiteSpace(token))
            {
                stderr.WriteLine($"axiom: set ${TokenVariable} to an access token.");
                return null;
            }

            return new AxiomClient(url, token.Trim(), handler);
        }
    }
}
