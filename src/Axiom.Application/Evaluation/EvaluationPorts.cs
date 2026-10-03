using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Domain.Audit;
using Axiom.Domain.Evaluation;

namespace Axiom.Application.Evaluation;

public sealed record StoredEvaluation(EvaluationRecord Evaluation, Receipt Receipt);

public sealed record EvaluationQuery
{
    public string? Repository { get; init; }

    public EvaluationStage? Stage { get; init; }

    public Verdict? Verdict { get; init; }

    public string? CommitSha { get; init; }

    public string? GovernanceId { get; init; }

    public DateTimeOffset? Since { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 50;
}

public sealed record EvaluationSummary(
    string Id,
    EvaluationStage Stage,
    Verdict Verdict,
    string Repository,
    string? CommitSha,
    string? PullRequest,
    string SnapshotId,
    DateTimeOffset CreatedAt,
    string ActorSubject,
    string? HarnessType,
    bool SignificantChange,
    int FindingCount,
    string ReceiptId);

/// <summary>
/// Append-only store of evaluations and their receipts (design.md §4.8). Nothing is ever updated or
/// deleted through this port.
/// </summary>
public interface IEvaluationStore
{
    Task<StoredEvaluation?> FindAsync(string organizationId, string evaluationId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the evaluation, issues its receipt on the organization's hash chain, and queues the given
    /// events, all in one transaction. If the evaluation ID already exists the stored one is returned
    /// unchanged, which makes retries and duplicate deliveries safe.
    /// </summary>
    Task<StoredEvaluation> AppendAsync(EvaluationRecord evaluation, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken);

    Task<Receipt?> FindReceiptAsync(string organizationId, string receiptOrEvaluationId, CancellationToken cancellationToken);

    /// <summary>The most recent receipt of the given stage for an exact commit, if any.</summary>
    Task<Receipt?> FindReceiptByCommitAsync(string organizationId, string repository, string commitSha, EvaluationStage? stage, CancellationToken cancellationToken);

    Task<(ImmutableArray<EvaluationSummary> Items, int Total)> SearchAsync(string organizationId, EvaluationQuery query, CancellationToken cancellationToken);

    /// <summary>Receipts in chain order, for integrity verification.</summary>
    IAsyncEnumerable<Receipt> ReadChainAsync(string organizationId, CancellationToken cancellationToken);
}
