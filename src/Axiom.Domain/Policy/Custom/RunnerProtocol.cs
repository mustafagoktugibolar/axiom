using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Axiom.Domain.Policy.Custom;

/// <summary>One added line in the wire format.</summary>
public sealed record WireAddedLine(int Number, string Text);

/// <summary>One changed file in the wire format. Kind is lower-case: added, modified, deleted, renamed.</summary>
public sealed record WireChange(
    string Path,
    string Kind,
    string? PreviousPath,
    string? Content,
    string? PreviousContent,
    List<WireAddedLine> AddedLines);

/// <summary>The bounded slice of <see cref="PolicyContext"/> a custom rule can see: changes and the tree's path list, never other file contents.</summary>
public sealed record WireContext(string Repository, List<WireChange> Changes, List<string> TreePaths);

/// <summary>Request written to the runner's stdin as one UTF-8 JSON document.</summary>
public sealed record RunnerRequest(
    int Protocol,
    string RuleId,
    string Spec,
    Dictionary<string, string> Parameters,
    WireContext Context);

public sealed record WireViolation(string Message, string? Path, int? Line, string? Evidence);

/// <summary>Result read from the runner's stdout. Outcome is <c>passed</c>, <c>violated</c> or <c>error</c>; an error carries a stable <see cref="CustomRuleCodes"/> code.</summary>
public sealed record RunnerResponse(int Protocol, string Outcome, List<WireViolation> Violations, string? Code, string? Message);

/// <summary>
/// The runner protocol (docs/custom-policy-runner.md). Requests and responses are single JSON documents with a
/// protocol version; the codec enforces size and shape limits on both directions.
/// </summary>
public static class RunnerProtocol
{
    public const int Version = 1;
    public const int DefaultMaxInputBytes = 4 * 1024 * 1024;
    public const int DefaultMaxOutputBytes = 256 * 1024;
    public const int MaxFieldCharacters = 2_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static byte[] Serialize(RunnerRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);

    public static byte[] Serialize(RunnerResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, Options);

    public static RunnerRequest ParseRequest(ReadOnlySpan<byte> bytes)
    {
        RunnerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RunnerRequest>(bytes, Options);
        }
        catch (JsonException ex)
        {
            throw new PolicyRuleExecutionException(CustomRuleCodes.BadRequest, "The request is not valid JSON.", ex);
        }

        if (request is null || request.Protocol != Version || request.Spec is null || request.Context?.Changes is null
            || request.Context.TreePaths is null || request.Parameters is null)
        {
            throw new PolicyRuleExecutionException(CustomRuleCodes.BadRequest, "The request is incomplete or has an unsupported protocol version.");
        }

        ValidateShape(request);
        return request;
    }

    /// <summary>
    /// Rejects anything the evaluator must not have to reason about: nulls inside collections, control characters,
    /// and paths that climb with <c>..</c> (a repository cannot contain them; accepting them would let a name evade a selector).
    /// </summary>
    private static void ValidateShape(RunnerRequest request)
    {
        var changes = request.Context.Changes;
        if (changes.Any(c => c is null || !IsSafePath(c.Path) || (c.PreviousPath is not null && !IsSafePath(c.PreviousPath)))
            || request.Context.TreePaths.Any(p => !IsSafePath(p))
            || request.Parameters.Any(p => p.Value is null)
            || changes.Any(c => c.AddedLines is not null && c.AddedLines.Any(l => l is null || l.Number < 1 || l.Text is null)))
        {
            throw new PolicyRuleExecutionException(CustomRuleCodes.BadRequest, "The request contains a missing value, an invalid path or an invalid line number.");
        }
    }

    private static bool IsSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
        {
            return false;
        }

        return !path.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal);
    }

    /// <summary>Parses and validates a runner response; anything out of bounds is <see cref="CustomRuleCodes.MalformedOutput"/>.</summary>
    public static RunnerResponse ParseResponse(ReadOnlySpan<byte> bytes)
    {
        RunnerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<RunnerResponse>(bytes, Options);
        }
        catch (JsonException ex)
        {
            throw new PolicyRuleExecutionException(CustomRuleCodes.MalformedOutput, "The runner output is not valid JSON.", ex);
        }

        if (response is null || response.Protocol != Version || response.Violations is null
            || response.Outcome is not ("passed" or "violated" or "error")
            || response.Violations.Count > CustomRuleEvaluator.MaxViolations + 1
            || response.Violations.Any(v => v is null || v.Message is null || v.Message.Length > MaxFieldCharacters
                || (v.Path?.Length ?? 0) > MaxFieldCharacters || (v.Evidence?.Length ?? 0) > MaxFieldCharacters
                || v.Line is <= 0)
            || (response.Outcome == "passed" && response.Violations.Count > 0)
            || (response.Outcome == "violated" && response.Violations.Count == 0)
            || (response.Outcome == "error" && string.IsNullOrEmpty(response.Code)))
        {
            throw new PolicyRuleExecutionException(CustomRuleCodes.MalformedOutput, "The runner output does not match the result schema.");
        }

        return response;
    }

    public static WireContext ToWire(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new WireContext(
            context.Repository,
            [.. (context.Changes.IsDefault ? [] : context.Changes).Select(c => new WireChange(
                c.Path,
                KindName(c.Kind),
                c.PreviousPath,
                c.Content,
                c.PreviousContent,
                [.. (c.AddedLines.IsDefault ? [] : c.AddedLines).Select(l => new WireAddedLine(l.Number, l.Text))]))],
            [.. context.Tree.Paths]);
    }

    public static ImmutableArray<ChangedFile> FromWire(WireContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [.. context.Changes.Select(c => new ChangedFile(
            c.Path ?? throw Bad(),
            ParseKind(c.Kind),
            c.PreviousPath,
            c.Content,
            c.PreviousContent,
            [.. (c.AddedLines ?? []).Select(l => new AddedLine(l.Number, l.Text ?? throw Bad()))]))];

        static PolicyRuleExecutionException Bad() => new(CustomRuleCodes.BadRequest, "A change is missing a required field.");
    }

    private static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Modified => "modified",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ChangeKind ParseKind(string? kind) => kind switch
    {
        "added" => ChangeKind.Added,
        "modified" => ChangeKind.Modified,
        "deleted" => ChangeKind.Deleted,
        "renamed" => ChangeKind.Renamed,
        _ => throw new PolicyRuleExecutionException(CustomRuleCodes.BadRequest, "A change has an unknown kind."),
    };
}

/// <summary>
/// The runner's core: bytes in, bytes out, no I/O of its own, no clock, no network. The
/// <c>Axiom.PolicyRunner</c> executable wraps this with stdin/stdout and the process-level guards.
/// </summary>
public static class CustomRuleRunnerCore
{
    public static byte[] Run(ReadOnlySpan<byte> requestBytes, long stepBudget = CustomRuleEvaluator.DefaultStepBudget)
    {
        try
        {
            var request = RunnerProtocol.ParseRequest(requestBytes);
            var spec = CustomRuleSpec.Parse(request.Spec).Bind(request.Parameters);
            var violations = CustomRuleEvaluator.Evaluate(
                spec,
                RunnerProtocol.FromWire(request.Context),
                request.Context.TreePaths,
                stepBudget);

            return RunnerProtocol.Serialize(new RunnerResponse(
                RunnerProtocol.Version,
                violations.IsEmpty ? "passed" : "violated",
                [.. violations.Select(v => new WireViolation(
                    Truncate(v.Message)!, Truncate(v.Path), v.Line, Truncate(v.Evidence)))],
                null,
                null));
        }
        catch (CustomRuleException ex)
        {
            return Error(ex.Code, ex.Message);
        }
        catch (PolicyRuleExecutionException ex)
        {
            return Error(ex.Code, ex.Message);
        }
#pragma warning disable CA1031 // Defense in depth: whatever the evaluator did not anticipate is a coded crash, never an unhandled exception that skips the protocol.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return Error(CustomRuleCodes.Crash, "The rule evaluation failed unexpectedly.");
        }
    }

    private static byte[] Error(string code, string message) =>
        RunnerProtocol.Serialize(new RunnerResponse(RunnerProtocol.Version, "error", [], code, Truncate(message)));

    private static string? Truncate(string? value) =>
        value is null || value.Length <= RunnerProtocol.MaxFieldCharacters ? value : value[..RunnerProtocol.MaxFieldCharacters];
}
