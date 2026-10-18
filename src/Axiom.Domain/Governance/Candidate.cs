using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Axiom.Domain.Governance;

/// <summary>
/// Lifecycle of a candidate. <see cref="MergedAsRecord"/> only records that a human-approved record
/// reached the governance Git repository; the record, not the candidate, is what governs.
/// </summary>
public enum CandidateState
{
    Proposed,
    Approved,
    Rejected,
    MergedAsRecord,
}

/// <summary>The governance record a candidate would become, as a draft for a human to commit.</summary>
public sealed record CandidateDraft(
    RecordKind Kind,
    string Title,
    string Statement,
    string Rationale,
    ImmutableSortedDictionary<string, ImmutableArray<string>> ScopeHints);

/// <summary>
/// Knowledge generated from an external source (an ADR, a doc, a review thread) that might become a
/// governance record. A candidate is never authoritative: it takes no part in resolution or evaluation,
/// and nothing here can turn it into a record. Only a human-approved record merged into the governance
/// Git repository governs (ADR-0001). Transitions return a new instance; illegal ones throw.
/// </summary>
public sealed record Candidate
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    /// <summary>Where the knowledge came from, how it was extracted and how sure the extractor is.</summary>
    public required CandidateEvidence Evidence { get; init; }

    public required CandidateDraft Draft { get; init; }

    public CandidateState State { get; init; } = CandidateState.Proposed;

    public required DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;

    public DateTimeOffset? DecidedAt { get; init; }

    public string? DecidedBy { get; init; }

    public string? DecisionComment { get; init; }

    /// <summary>The governance record ID this candidate became, once merged.</summary>
    public string? MergedRecordId { get; init; }

    /// <summary>Always false: a candidate never governs, whatever its state.</summary>
#pragma warning disable CA1822 // Deliberately an instance member: it is part of the candidate's contract and of its stored and shown shape.
    public bool IsAuthoritative => false;
#pragma warning restore CA1822

    /// <summary>Candidates are identified by organization and excerpt, so re-extracting the same knowledge is a no-op.</summary>
    public static string IdFor(string organizationId, string excerptHash)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"axiom-candidate-v1\n{organizationId}\n{excerptHash}"));
        return "cnd_" + Convert.ToHexStringLower(digest.AsSpan(0, 12));
    }

    public Candidate Approve(string decidedBy, string comment, DateTimeOffset at) =>
        Decide(CandidateState.Approved, decidedBy, comment, at);

    public Candidate Reject(string decidedBy, string comment, DateTimeOffset at) =>
        Decide(CandidateState.Rejected, decidedBy, comment, at);

    public Candidate MarkMerged(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return State == CandidateState.Approved
            ? this with { State = CandidateState.MergedAsRecord, MergedRecordId = recordId }
            : throw new InvalidOperationException($"A {State} candidate cannot be marked merged; only an approved one can.");
    }

    public static bool CanTransition(CandidateState from, CandidateState to) => (from, to) switch
    {
        (CandidateState.Proposed, CandidateState.Approved) => true,
        (CandidateState.Proposed, CandidateState.Rejected) => true,
        (CandidateState.Approved, CandidateState.MergedAsRecord) => true,
        _ => false,
    };

    private Candidate Decide(CandidateState to, string decidedBy, string comment, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        return CanTransition(State, to)
            ? this with { State = to, DecidedBy = decidedBy, DecisionComment = comment, DecidedAt = at }
            : throw new InvalidOperationException($"A {State} candidate cannot become {to}.");
    }
}
