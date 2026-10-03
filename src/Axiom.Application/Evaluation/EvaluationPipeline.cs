using System.Collections.Immutable;
using System.Diagnostics;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;

namespace Axiom.Application.Evaluation;

/// <summary>Stage-independent description of what is being evaluated.</summary>
public sealed record EvaluationRequest
{
    public required AxiomPrincipal Principal { get; init; }

    public required EvaluationStage Stage { get; init; }

    public required string Repository { get; init; }

    public string? Ref { get; init; }

    public string? CommitSha { get; init; }

    public string? BaseSha { get; init; }

    public string? PullRequest { get; init; }

    public string? Task { get; init; }

    /// <summary>Null when the request does not determine the changed paths.</summary>
    public ImmutableArray<string>? Paths { get; init; }

    public string? Environment { get; init; }

    public string? HarnessType { get; init; }

    public string? HarnessSessionId { get; init; }

    public string? ParentEvaluationId { get; init; }

    public string? DesignId { get; init; }

    public string? DesignHash { get; init; }

    /// <summary>True for merge gates: an unresolved authority conflict must then block (design.md §6).</summary>
    public bool HardGate { get; init; }

    /// <summary>Additional systems/repositories the change declares it touches (from a design).</summary>
    public int DeclaredSystemCount { get; init; }

    public int DeclaredRepositoryCount { get; init; }

    /// <summary>Stage-specific inputs that must distinguish otherwise identical requests (e.g. policy catalog hash).</summary>
    public ImmutableArray<string> FingerprintExtras { get; init; } = [];
}

/// <summary>
/// An evaluation in progress: governance has been resolved, and the stage adds its own findings before
/// the pipeline finalizes the verdict and receipt.
/// </summary>
public sealed class EvaluationSession
{
    internal EvaluationSession(EvaluationRequest request, string id, string fingerprint, DateTimeOffset startedAt, long startedTimestamp)
    {
        Request = request;
        Id = id;
        Fingerprint = fingerprint;
        StartedAt = startedAt;
        StartedTimestamp = startedTimestamp;
    }

    public EvaluationRequest Request { get; }

    public string Id { get; }

    public string Fingerprint { get; }

    public DateTimeOffset StartedAt { get; }

    internal long StartedTimestamp { get; }

    /// <summary>Set when an identical evaluation already exists; the stage must return it untouched.</summary>
    public StoredEvaluation? Existing { get; internal init; }

    public GovernanceSnapshot Snapshot { get; internal init; } = null!;

    public SnapshotInfo SnapshotInfo { get; internal init; } = null!;

    public ChangeContext Context { get; internal init; } = null!;

    public ResolutionResult Resolution { get; internal init; } = null!;

    public SignificanceAssessment Significance { get; set; } = new(false, [], []);

    public ImmutableArray<AffectedDependency> AffectedDependencies { get; internal init; } = [];

    public List<Finding> Findings { get; } = [];

    public List<string> RequiredActions { get; } = [];

    public List<PolicyRunResult> PolicyRuns { get; } = [];

    public SemanticCoverage SemanticCoverage { get; set; } = SemanticCoverage.NotRequested;
}

/// <summary>
/// The Evaluation Orchestrator core (design.md §4.7). Every stage (preflight, design, diff, PR) goes
/// through the same resolution and the same finalization, so REST, MCP and the CLI cannot diverge.
/// </summary>
public sealed class EvaluationPipeline(
    IGovernanceSnapshotProvider snapshots,
    ChangeScopeResolver scopes,
    ISystemGraph graph,
    IEvaluationStore store,
    TimeProvider time)
{
    public const string EngineVersion = "1";
    public const string DesignRequired = "DESIGN_REQUIRED";
    public const int DependencyDepth = 2;

    public async Task<EvaluationSession> BeginAsync(EvaluationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Principal.OrganizationId;
        var now = time.GetUtcNow();
        var started = time.GetTimestamp();

        var info = await snapshots.GetCurrentInfoAsync(organization, cancellationToken)
            ?? throw AxiomException.Unavailable(ErrorCodes.NoPublishedSnapshot, "No governance snapshot has been published for this organization; the change cannot be evaluated.");
        var snapshot = await snapshots.GetAsync(organization, info.SnapshotId, cancellationToken)
            ?? throw AxiomException.Unavailable(ErrorCodes.GovernanceUnavailable, $"Governance snapshot '{info.SnapshotId}' could not be loaded.");

        var context = await scopes.ResolveAsync(new ChangeScopeRequest(organization, request.Repository, request.Paths, request.Environment), cancellationToken);

        var fingerprint = EvaluationIdentity.Fingerprint(
        [
            request.Stage.ToString(), organization, request.Principal.Subject, context.RepositoryName, request.Ref, request.CommitSha, request.BaseSha,
            request.PullRequest, request.Task, request.Paths is { } p ? string.Join("\n", p.Select(GlobPattern.NormalizePath).Order(StringComparer.Ordinal)) : null,
            request.Environment, request.HarnessType, request.ParentEvaluationId, request.DesignId, request.DesignHash, request.HardGate ? "gate" : "advisory",
            .. request.FingerprintExtras,
        ]);
        var id = EvaluationIdentity.EvaluationId(fingerprint, snapshot.Id, context.CatalogVersion, EngineVersion, EvaluationIdentity.ValidityEpoch(snapshot, now));

        if (await store.FindAsync(organization, id, cancellationToken) is { } existing)
        {
            AxiomTelemetry.EvaluationCacheHits.Add(1, new KeyValuePair<string, object?>("stage", request.Stage.ToString()));
            return new EvaluationSession(request, id, fingerprint, now, started) { Existing = existing };
        }

        var (resolution, significance, change) = Resolve(request, snapshot, context, now);
        var dependencies = await FindAffectedDependenciesAsync(organization, context, cancellationToken);

        var session = new EvaluationSession(request, id, fingerprint, now, started)
        {
            Snapshot = snapshot,
            SnapshotInfo = info,
            Context = context with { Scope = change },
            Resolution = resolution,
            Significance = significance,
            AffectedDependencies = dependencies,
        };

        session.Findings.AddRange(context.Findings);
        session.Findings.AddRange(resolution.Conflicts.Select(c => ConflictFinding(c, resolution)));
        return session;
    }

    public async Task<StoredEvaluation> CompleteAsync(EvaluationSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Existing is { } existing)
        {
            return existing;
        }

        var request = session.Request;
        var findings = session.Findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToImmutableArray();
        var verdict = VerdictLattice.Combine(findings);

        var reviewers = verdict >= Verdict.RequireReview
            ? findings.Where(f => !f.IsWaived && f.Severity >= EnforcementLevel.RequireReview)
                .SelectMany(f => f.GovernanceIds)
                .Select(id => session.Snapshot.Find(id))
                .Where(r => r is not null)
                .SelectMany(r => r!.Record.Owners)
                .Concat(session.Context.Topology?.Owners.Select(o => o.ToString()) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray()
            : [];

        var evaluation = new EvaluationRecord
        {
            Id = session.Id,
            OrganizationId = request.Principal.OrganizationId,
            Stage = request.Stage,
            CreatedAt = session.StartedAt,
            Actor = new ActorIdentity(request.Principal.Subject, request.Principal.DisplayName, request.HarnessType, request.HarnessSessionId),
            Scm = new ScmCoordinates(session.Context.RepositoryName, request.Ref, request.CommitSha, request.BaseSha, request.PullRequest),
            Task = request.Task,
            RequestFingerprint = session.Fingerprint,
            SnapshotId = session.Snapshot.Id,
            SourceCommit = session.Snapshot.SourceCommit,
            SnapshotPublishedAt = session.SnapshotInfo.PublishedAt,
            CatalogVersion = session.Context.CatalogVersion,
            ParentEvaluationId = request.ParentEvaluationId,
            DesignId = request.DesignId,
            DesignHash = request.DesignHash,
            ResolvedScope = ResolvedScopeView.From(session.Context.Scope, session.Context.Gaps),
            AppliedRecords = [.. session.Resolution.Applicable.Select(AppliedRecordRef.From)],
            AppliedExceptions = [.. session.Resolution.Exceptions.Select(AppliedExceptionRef.From)],
            RequiredChecks = RequiredChecks(session.Resolution),
            PolicyRuns = [.. session.PolicyRuns],
            Findings = findings,
            Significance = session.Significance,
            RequiredActions = [.. session.RequiredActions.Distinct(StringComparer.Ordinal)],
            RequiredReviewers = reviewers,
            AffectedDependencies = session.AffectedDependencies,
            SemanticCoverage = session.SemanticCoverage,
            Trace = session.Resolution.Trace,
            Verdict = verdict,
        };

        StoredEvaluation stored;
        using (AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.ReceiptEmit))
        {
            try
            {
                stored = await store.AppendAsync(evaluation, Events(evaluation), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AxiomTelemetry.ReceiptFailures.Add(1);
                throw AxiomException.Unavailable(ErrorCodes.GovernanceUnavailable, "The evaluation could not be recorded, so no verdict is issued.", ex);
            }
        }

        RecordMetrics(session, stored.Evaluation);
        return stored;
    }

    private static (ResolutionResult Resolution, SignificanceAssessment Significance, EvaluationScope Change) Resolve(
        EvaluationRequest request, GovernanceSnapshot snapshot, ChangeContext context, DateTimeOffset now)
    {
        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.ResolveGovernance);
        var records = snapshot.Revisions.ToArray();

        // First pass leaves the change class undetermined, which yields a superset of applicable records;
        // their bindings supply the organization-defined significance triggers.
        var preliminary = DecisionResolver.Resolve(new ResolutionInput(records, context.Scope, now, request.HardGate));
        var topology = context.Topology;
        var significance = SignificantChangeClassifier.Classify(new SignificanceInput(
            request.Task,
            request.Paths ?? [],
            Math.Max(topology?.Systems.Length ?? 0, request.DeclaredSystemCount),
            Math.Max(1, request.DeclaredRepositoryCount),
            CrossSystemDependency: false,
            SignificantChangeClassifier.TriggersFrom(preliminary.Effective.Select(a => a.Record))));

        // The change class is only determined once the changed paths are known.
        if (request.Paths is null)
        {
            Tag(activity, preliminary);
            return (preliminary, significance, context.Scope);
        }

        var change = context.Scope.With(ScopeDimension.ChangeClass, significance.ChangeClasses);
        var resolution = DecisionResolver.Resolve(new ResolutionInput(records, change, now, request.HardGate));
        Tag(activity, resolution);
        return (resolution, significance, change);
    }

    private static void Tag(Activity? activity, ResolutionResult resolution)
    {
        activity?.SetTag("axiom.resolution.applicable", resolution.Applicable.Length);
        activity?.SetTag("axiom.resolution.conflicts", resolution.Conflicts.Length);
    }

    private async Task<ImmutableArray<AffectedDependency>> FindAffectedDependenciesAsync(string organization, ChangeContext context, CancellationToken cancellationToken)
    {
        if (context.Topology is not { } topology)
        {
            return [];
        }

        var found = new SortedDictionary<EntityRef, AffectedDependency>();
        var direct = topology.Components.Concat(topology.Apis).Concat(topology.Resources).Append(topology.Repository).ToHashSet();

        async Task CollectAsync(EntityRef start, ImmutableArray<RelationType> relations, int depth)
        {
            var result = await graph.TraverseAsync(organization, new ImpactQuery(start, TraversalDirection.Upstream, relations, depth, IncludeUnconfirmed: true), cancellationToken);
            foreach (var impacted in result.Impacted.Where(i => !direct.Contains(i.Entity)))
            {
                if (found.TryGetValue(impacted.Entity, out var known) && known.Depth <= impacted.Depth)
                {
                    continue;
                }

                var owners = result.Owners.TryGetValue(impacted.Entity, out var o) ? o.Select(x => x.ToString()).ToImmutableArray() : [];
                found[impacted.Entity] = new AffectedDependency(
                    impacted.Entity.ToString(),
                    impacted.Depth,
                    [.. impacted.Path.Select(s => $"{s.From} -{s.Relation}-> {s.To}")],
                    owners,
                    impacted.IsFact);
            }
        }

        foreach (var component in topology.Components)
        {
            await CollectAsync(component, [RelationType.DependsOn], DependencyDepth);
        }

        foreach (var api in topology.Apis)
        {
            await CollectAsync(api, [RelationType.Consumes], 1);
        }

        foreach (var resource in topology.Resources)
        {
            await CollectAsync(resource, [RelationType.StoresIn, RelationType.SubscribesTo, RelationType.PublishesTo], 1);
        }

        return [.. found.Values];
    }

    private static Finding ConflictFinding(AuthorityConflict conflict, ResolutionResult resolution)
    {
        ImmutableArray<string> ids = [conflict.FirstRecordId, conflict.SecondRecordId];
        var evidence = ids
            .Select(id => resolution.Applicable.First(a => a.Revision.Id == id))
            .Select(a => new EvidenceRef("decision", a.Revision.Id, a.Revision.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Revision.Provenance.Path, a.Record.ContentHash))
            .ToImmutableArray();

        return new Finding
        {
            Code = conflict.Code,
            Severity = conflict.Severity,
            Source = FindingSource.Resolution,
            Message = conflict.Explanation,
            GovernanceIds = ids,
            Evidence = evidence,
            RecommendedAction = conflict.IsResolved
                ? $"No action needed: {conflict.PrevailingRecordId} prevails by precedence."
                : $"The owners of {conflict.FirstRecordId} and {conflict.SecondRecordId} must resolve this: clarify scope, supersede one record, record a new decision, grant a scoped exception, or reject the change.",
        };
    }

    private static ImmutableArray<RequiredCheck> RequiredChecks(ResolutionResult resolution) =>
    [
        .. resolution.Applicable
            .Where(a => a.RefinedBy.IsEmpty && a.OverriddenBy.IsEmpty)
            .SelectMany(a => a.Record.Enforcement.Rules
                .Where(r => r.RuleId != SignificantChangeClassifier.TriggerRuleId)
                .Select(r => new RequiredCheck(r.RuleId, a.Revision.Id, r.Mode)))
            .OrderBy(c => c.RecordId, StringComparer.Ordinal)
            .ThenBy(c => c.RuleId, StringComparer.Ordinal),
    ];

    private static List<IntegrationEvent> Events(EvaluationRecord evaluation)
    {
        var data = new
        {
            evaluationId = evaluation.Id,
            stage = evaluation.Stage.ToString(),
            repository = evaluation.Scm.Repository,
            @ref = evaluation.Scm.Ref,
            commitSha = evaluation.Scm.CommitSha,
            verdict = VerdictLattice.ToContract(evaluation.Verdict),
            snapshotId = evaluation.SnapshotId,
        };

        var events = new List<IntegrationEvent>
        {
            IntegrationEvent.Create(EventTypes.EvaluationCompleted, evaluation.OrganizationId, evaluation.CreatedAt, evaluation.Id, data),
        };
        if (evaluation.Verdict == Verdict.Block)
        {
            events.Add(IntegrationEvent.Create(EventTypes.EvaluationBlocked, evaluation.OrganizationId, evaluation.CreatedAt, evaluation.Id, data));
        }

        return events;
    }

    private void RecordMetrics(EvaluationSession session, EvaluationRecord evaluation)
    {
        var stage = new KeyValuePair<string, object?>("stage", evaluation.Stage.ToString());
        AxiomTelemetry.Evaluations.Add(1, stage, new KeyValuePair<string, object?>("verdict", VerdictLattice.ToContract(evaluation.Verdict)));
        AxiomTelemetry.EvaluationDuration.Record(time.GetElapsedTime(session.StartedTimestamp).TotalMilliseconds, stage);
        AxiomTelemetry.ResolutionContextSize.Record(evaluation.AppliedRecords.Length, stage);

        var unresolved = session.Resolution.Conflicts.Count(c => !c.IsResolved);
        if (unresolved > 0)
        {
            AxiomTelemetry.ResolutionConflicts.Add(unresolved, stage);
        }

        if (evaluation.AppliedExceptions.Length > 0)
        {
            AxiomTelemetry.ExceptionUsage.Add(evaluation.AppliedExceptions.Length, stage);
        }
    }
}
