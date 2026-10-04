using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Application.Governance;
using Axiom.Application.Query;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Axiom.Mcp;

[Description("The agent harness calling Axiom, for attribution in receipts.")]
public sealed record HarnessInput(
    [property: Description("Harness name, for example 'kiro', 'claude-code', 'codex'.")] string? Type,
    [property: Description("Harness session identifier.")] string? SessionId);

[Description("What is being changed.")]
public sealed record ChangeInput(
    [property: Description("Caller's classification of the change, for example 'api_or_contract'. Echoed back.")] string? Kind,
    [property: Description("Entity reference, for example 'api:orders/v2' or 'component:gui/api-gateway'.")] string? EntityRef);

/// <summary>
/// The MCP contract (docs/04-contracts/mcp-contract.md, ADR-0003). Each tool is a thin adapter over an
/// application use case, the same one REST calls, so the two surfaces cannot disagree. Tools never
/// expose connector credentials, and a failure is an error, never a verdict.
/// </summary>
[McpServerToolType]
public sealed class GovernanceTools(
    AxiomPrincipal principal,
    PreflightService preflight,
    DesignValidationService designs,
    DiffEvaluationService diff,
    GovernanceReadService governance,
    ImpactService impact,
    ContextService context,
    ReceiptService receipts,
    FindingExplanationService explanations,
    ExceptionRequestService exceptions,
    ILogger<GovernanceTools> logger)
{
    [McpServerTool(Name = "governance.preflight_change", Title = "Preflight a change", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Call before designing or editing. Returns the resolved scope, the authoritative governance records that apply, active exceptions, "
        + "whether the change is significant (needs a design), findings and the required next actions. Do not treat a tool error as ALLOW.")]
    public Task<EvaluationResult> PreflightChange(
        [Description("Repository name, namespaced name, reference or clone URL.")] string repository,
        [Description("Branch or ref being worked on.")] string @ref,
        [Description("What the change is meant to do, in plain language.")] string task,
        [Description("Paths the change is expected to touch (globs allowed).")] string[]? paths = null,
        [Description("Organization; must match the caller's organization.")] string? organization = null,
        [Description("Deployment environment, when relevant.")] string? environment = null,
        HarnessInput? harness = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "preflight_change", async () => EvaluationResult.From(await preflight.ExecuteAsync(
            principal, new PreflightRequest(organization, repository, @ref, task, paths, environment, harness?.Type, harness?.SessionId), cancellationToken)));

    [McpServerTool(Name = "governance.get_context", Title = "Get governance context", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the bounded governance context for an evaluation, or for an explicit repository and task: decisions, standards, goals, "
        + "active exceptions, topology neighbors, known failure modes and required checks.")]
    public Task<ContextBundleDto> GetContext(
        [Description("An earlier evaluation ID. Alternatively give repository and task.")] string? evaluationId = null,
        [Description("Repository, with 'task', when no evaluation exists yet.")] string? repository = null,
        [Description("Task description, with 'repository'.")] string? task = null,
        [Description("Branch or ref.")] string? @ref = null,
        [Description("Paths the change is expected to touch.")] string[]? paths = null,
        [Description("Include each record's Markdown rationale.")] bool includeRationale = false,
        HarnessInput? harness = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "get_context", () => context.GetAsync(
            principal, new ContextRequest(evaluationId, repository, @ref, task, paths, includeRationale, harness?.Type, harness?.SessionId), cancellationToken));

    [McpServerTool(Name = "governance.query_decisions", Title = "Query governance records", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches governance records. Results distinguish authoritative records from historical (superseded, deprecated, rejected) and candidate (proposed) ones: "
        + "only records with isAuthoritative=true govern.")]
    public Task<GovernanceSearchResult> QueryDecisions(
        [Description("Full-text query over statement, title, tags and ID.")] string? text = null,
        [Description("Exact record IDs.")] string[]? ids = null,
        [Description("Owner team.")] string? owner = null,
        [Description("Tag.")] string? tag = null,
        [Description("Repository the record is scoped to.")] string? repository = null,
        [Description("System the record is scoped to.")] string? system = null,
        [Description("Component the record is scoped to.")] string? component = null,
        [Description("Technology the record is scoped to.")] string? technology = null,
        [Description("Statuses: proposed, accepted, deprecated, superseded, rejected, expired.")] string[]? status = null,
        [Description("Kinds: principle, standard, decision, goal, exception.")] string[]? kind = null,
        [Description("Rows to skip.")] int skip = 0,
        [Description("Page size, at most 200.")] int take = 50,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "query_decisions", () =>
        {
            var scope = ImmutableDictionary<ScopeDimension, string>.Empty;
            foreach (var (dimension, value) in new[]
            {
                (ScopeDimension.Repository, repository), (ScopeDimension.System, system), (ScopeDimension.Component, component), (ScopeDimension.Technology, technology),
            })
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    scope = scope.Add(dimension, value.Trim());
                }
            }

            return governance.SearchAsync(
                principal,
                new GovernanceQuery
                {
                    Text = text,
                    Ids = [.. ids ?? []],
                    Owner = owner,
                    Tag = tag,
                    Scope = scope,
                    Statuses = [.. (status ?? []).Select(s => ParseEnum<LifecycleStatus>(s, "status"))],
                    Kinds = [.. (kind ?? []).Select(k => ParseEnum<RecordKind>(k, "kind"))],
                    Skip = skip,
                    Take = take,
                },
                cancellationToken);
        });

    [McpServerTool(Name = "governance.get_decision", Title = "Get a governance record", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns one governance record with its enforcement bindings, relations, revision history and the records that point to it.")]
    public Task<GovernanceDetailDto> GetDecision(
        [Description("Record ID, for example 'ARCH-042'.")] string id,
        [Description("Include the Markdown rationale.")] bool includeRationale = false,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "get_decision", () => governance.GetAsync(principal, id, includeRationale, cancellationToken));

    [McpServerTool(Name = "governance.impact_analysis", Title = "Analyze impact", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the entities a change reaches with relationship paths, owners, confirmed vs candidate provenance and the governance records scoped to them.")]
    public Task<ImpactResultDto> ImpactAnalysis(
        [Description("Repository the change is in. Used when the change has no entityRef.")] string? repository = null,
        ChangeInput? change = null,
        [Description("How many hops to follow, 1 to 6.")] int depth = ImpactService.DefaultDepth,
        [Description("Also follow inferred, unconfirmed relations (reported as candidates).")] bool includeUnconfirmed = false,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "impact_analysis", () => impact.AnalyzeAsync(
            principal, new ImpactRequestDto(repository, change?.Kind, change?.EntityRef, depth, includeUnconfirmed), cancellationToken));

    [McpServerTool(Name = "governance.validate_design", Title = "Validate a design", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Validates a design for a significant change against applicable governance before implementation. Provide exactly one of 'design' (structured) or 'designMarkdown'. "
        + "The resulting evaluation records the approved scope and the design's material hash. REQUIRE_REVIEW means stop and surface it to a human.")]
    public Task<EvaluationResult> ValidateDesign(
        [Description("Repository the design is for.")] string repository,
        [Description("Branch or ref.")] string @ref,
        [Description("Structured design (design.schema.json).")] JsonElement? design = null,
        [Description("The design as Markdown with one heading per section.")] string? designMarkdown = null,
        [Description("The preflight evaluation this continues.")] string? preflightEvaluationId = null,
        [Description("Organization; must match the caller's organization.")] string? organization = null,
        HarnessInput? harness = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "validate_design", async () => EvaluationResult.From(await designs.ExecuteAsync(
            principal, new DesignValidationRequest(organization, repository, @ref, design, designMarkdown, preflightEvaluationId, harness?.Type, harness?.SessionId), cancellationToken)));

    [McpServerTool(Name = "governance.validate_diff", Title = "Validate a diff", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Validates the actual change between two immutable commits: recomputes scope from the changed files, detects scope expansion beyond the validated design, "
        + "runs the deterministic rules and returns the verdict with required actions. The diff is authoritative over the plan.")]
    public Task<EvaluationResult> ValidateDiff(
        [Description("Repository name, reference or clone URL.")] string repository,
        [Description("Base commit SHA.")] string baseSha,
        [Description("Head commit SHA.")] string headSha,
        [Description("The design evaluation this change implements, if it was significant.")] string? designEvaluationId = null,
        [Description("Branch or ref.")] string? @ref = null,
        [Description("Organization; must match the caller's organization.")] string? organization = null,
        HarnessInput? harness = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "validate_diff", async () => EvaluationResult.From(await diff.ExecuteAsync(
            principal, new DiffValidationRequest(organization, repository, baseSha, headSha, @ref, null, designEvaluationId, harness?.Type, harness?.SessionId), cancellationToken)));

    [McpServerTool(Name = "governance.get_receipt", Title = "Get a receipt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the immutable receipt of an evaluation, by receipt or evaluation ID, or by repository and commit SHA. The receipt states whether its digest verifies.")]
    public Task<ReceiptDto> GetReceipt(
        [Description("Receipt or evaluation ID.")] string? id = null,
        [Description("Repository, with commitSha.")] string? repository = null,
        [Description("Commit SHA, with repository.")] string? commitSha = null,
        [Description("Stage filter for the repository lookup: preflight, design, diff, pullrequest.")] string? stage = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "get_receipt", () =>
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                return receipts.GetAsync(principal, id, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(repository) && !string.IsNullOrWhiteSpace(commitSha))
            {
                return receipts.GetByCommitAsync(principal, repository, commitSha, string.IsNullOrWhiteSpace(stage) ? null : ParseEnum<EvaluationStage>(stage, "stage"), cancellationToken);
            }

            throw AxiomException.Invalid("Provide 'id', or both 'repository' and 'commitSha'.");
        });

    [McpServerTool(Name = "governance.explain_finding", Title = "Explain a finding", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains why a finding was raised: the selected rules, the resolution trace, the evidence, remediation options and the authority owners.")]
    public Task<FindingExplanationDto> ExplainFinding(
        [Description("Evaluation that produced the finding.")] string evaluationId,
        [Description("Finding code, for example 'POLICY_VIOLATION'.")] string code,
        [Description("File path, to pick one of several findings with the same code.")] string? path = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "explain_finding", () => explanations.ExplainAsync(principal, evaluationId, code, path, cancellationToken));

    [McpServerTool(Name = "governance.request_exception", Title = "Request an exception", ReadOnly = false, Idempotent = true, OpenWorld = false)]
    [Description("Requests a scoped, expiring exception to one or more governance records. This never grants the exception: it validates the request, routes it to the "
        + "owners of the target records and returns a draft record. The exception applies only after owners approve it and the record is accepted in the governance repository. "
        + "Hard (non-exemptable) controls cannot be waived. Do not proceed as if the exception existed.")]
    public Task<ExceptionRequestDto> RequestException(
        [Description("IDs of the governance records to deviate from, for example ['ARCH-042'].")] string[] targets,
        [Description("Exact scope, by dimension: {\"repositories\":[\"gateway\"],\"paths\":[\"src/Legacy/**\"]}. Must lie inside the targets' scope.")] Dictionary<string, string[]> scope,
        [Description("Why the deviation is needed, at least 20 characters.")] string rationale,
        [Description("When the exception ends (UTC). At most 180 days after it starts.")] DateTimeOffset expiresAt,
        [Description("Issue or migration plan that ends the deviation.")] string trackingIssue,
        [Description("Short title; defaults to one derived from the targets.")] string? title = null,
        [Description("When it starts (UTC); defaults to now.")] DateTimeOffset? startsAt = null,
        [Description("Measures that limit the risk while the exception is active.")] string[]? compensatingControls = null,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "request_exception", () => exceptions.RequestAsync(
            principal, new ExceptionRequestInput(targets, scope, title, rationale, startsAt, expiresAt, trackingIssue, compensatingControls), cancellationToken));

    [McpServerTool(Name = "governance.get_exception_request", Title = "Get an exception request", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the status of an exception request: pending, approved or rejected, with the decision and the draft governance record.")]
    public Task<ExceptionRequestDto> GetExceptionRequest(
        [Description("Exception request ID returned by governance.request_exception.")] string id,
        CancellationToken cancellationToken = default) =>
        McpErrors.RunAsync(logger, "get_exception_request", () => exceptions.GetAsync(principal, id, cancellationToken));

    private static T ParseEnum<T>(string value, string name)
        where T : struct, Enum =>
        Enum.TryParse<T>(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw AxiomException.Invalid($"'{value}' is not a valid {name}.");
}
