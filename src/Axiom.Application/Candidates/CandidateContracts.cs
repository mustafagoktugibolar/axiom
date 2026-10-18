using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Domain.Governance;

namespace Axiom.Application.Candidates;

/// <summary>Knowledge as an extractor hands it over. The service validates it; nothing here is trusted.</summary>
public sealed record CandidateInput(
    string? SourceLocator,
    string? ExcerptHash,
    string? ExtractionMethod,
    double Confidence,
    string? SuggestedOwner,
    RecordKind Kind,
    string? Title,
    string? Statement,
    string? Rationale,
    IReadOnlyDictionary<string, string[]>? ScopeHints);

public sealed record CandidateIngestResult(int Created, int Existing, ImmutableArray<string> CreatedIds, ImmutableArray<string> ExistingIds);

public sealed record CandidateListQuery(CandidateState? State, RecordKind? Kind, string? Owner, int Skip, int Take);

/// <param name="Created">False when a candidate for the same excerpt already existed; the stored one is returned unchanged.</param>
public sealed record CandidateAddResult(Candidate Candidate, bool Created);

/// <summary>
/// Store of candidates. A candidate is keyed by organization and ID (derived from the excerpt hash), so adding
/// the same knowledge twice is a no-op. Candidates are never read by resolution or evaluation.
/// </summary>
public interface ICandidateStore
{
    Task<Candidate?> FindAsync(string organizationId, string candidateId, CancellationToken cancellationToken);

    /// <summary>Stores the candidate and queues its events in one transaction. An existing ID returns the stored candidate unchanged.</summary>
    Task<CandidateAddResult> AddAsync(Candidate candidate, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the stored candidate with <paramref name="updated"/> when it is still in <paramref name="expected"/>
    /// state, queueing its events in the same transaction. Returns false when another decision won. Used by the approval workflow.
    /// </summary>
    Task<bool> UpdateAsync(Candidate updated, CandidateState expected, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken);

    Task<(ImmutableArray<Candidate> Items, int Total)> ListAsync(string organizationId, CandidateListQuery query, CancellationToken cancellationToken);
}

/// <summary>A candidate as REST shows it. <see cref="Authoritative"/> is always false: only the merged record governs.</summary>
public sealed record CandidateDto(
    string Id,
    string State,
    bool Authoritative,
    string Kind,
    string Title,
    string Statement,
    string SourceLocator,
    string ExcerptHash,
    string ExtractionMethod,
    double Confidence,
    string? SuggestedOwner,
    ImmutableSortedDictionary<string, ImmutableArray<string>> ScopeHints,
    ImmutableArray<string> Owners,
    string DraftRecordPath,
    string DraftRecord,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    string? DecisionComment,
    string? MergedRecordId,
    string NextStep);
