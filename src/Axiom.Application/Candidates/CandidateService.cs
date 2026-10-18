using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Domain.Governance;

namespace Axiom.Application.Candidates;

/// <summary>
/// Intake and read side of the knowledge-onboarding candidates (R17). A candidate carries evidence, is routed to
/// a suggested owner and renders as a draft record, but it never governs: nothing here feeds resolution or
/// evaluation, and Axiom does not write to the governance repository (ADR-0001). Approval and rejection are decided once by
/// a human owner (see DecideAsync); approving changes nothing in evaluation.
/// </summary>
public sealed class CandidateService(ICandidateStore store, TimeProvider time)
{
    public const int MaxBatch = 500;
    public const int MaxScopeValues = 50;
    public const int MaxPageSize = 200;
    public const int MinimumCommentLength = 10;
    public const string AgentProposalMethod = "agent-proposal";
    public const int MaxAgentStatementLength = 2_000;
    public const int MaxAgentRationaleLength = 8_000;
    public const int MaxAgentScopeValues = 20;
    public const double AgentProposalConfidence = 0.5;

    public const string NextStepProposed =
        "Proposed. This candidate does not govern. A human owner reviews it, then commits the draft record to the governance repository and merges it as accepted.";
    public const string NextStepApproved = "Approved by a reviewer. It still governs nothing until the draft record is merged as accepted in the governance repository.";
    public const string NextStepRejected = "Rejected. Nothing was added to governance.";
    public const string NextStepMerged = "Merged. The accepted record in the governance repository governs; this candidate only documents where it came from.";

    /// <summary>
    /// Stores candidates generated from an external source. Idempotent by excerpt: knowledge already captured
    /// is reported as existing and left untouched, whatever state it has reached.
    /// </summary>
    public async Task<CandidateIngestResult> IngestAsync(AxiomPrincipal principal, IReadOnlyCollection<CandidateInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(inputs);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
        if (inputs.Count > MaxBatch)
        {
            throw AxiomException.Invalid($"At most {MaxBatch} candidates may be submitted at once.");
        }

        var now = time.GetUtcNow();
        var created = ImmutableArray.CreateBuilder<string>();
        var existing = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var candidate = Build(principal, input, now);
            if (!seen.Add(candidate.Id))
            {
                existing.Add(candidate.Id);
                continue;
            }

            var data = new
            {
                candidateId = candidate.Id,
                kind = candidate.Draft.Kind.ToString(),
                title = candidate.Draft.Title,
                source = candidate.Evidence.SourceLocator,
                suggestedOwner = candidate.Evidence.SuggestedOwner,
            };
            var result = await store.AddAsync(
                candidate, [IntegrationEvent.Create(EventTypes.CandidateDecisionCreated, principal.OrganizationId, now, candidate.Id, data)], cancellationToken);
            (result.Created ? created : existing).Add(candidate.Id);
        }

        return new CandidateIngestResult(created.Count, existing.Count, created.ToImmutable(), existing.ToImmutable());
    }

    /// <summary>
    /// An agent (or contributor) proposes a decision. It becomes a Proposed, never-authoritative candidate with
    /// extraction method <see cref="AgentProposalMethod"/>, vetted by the same validator as imported candidates.
    /// Inputs are tighter than for imports because the caller is untrusted.
    /// </summary>
    public async Task<CandidateAddResult> ProposeAsync(AxiomPrincipal principal, AgentProposalInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(input);
        Authorizer.Demand(principal, AccessRight.ProposeDecision);

        var locator = RequestLimits.Identifier(input.EvidenceLocator, "evidenceLocator");
        var statement = RequestLimits.Text(input.Statement, "statement", MaxAgentStatementLength)?.Trim();
        var rationale = RequestLimits.Text(input.Rationale, "rationale", MaxAgentRationaleLength)?.Trim() ?? string.Empty;
        foreach (var (key, values) in input.ScopeHints ?? new Dictionary<string, string[]>())
        {
            if ((values?.Length ?? 0) > MaxAgentScopeValues)
            {
                throw AxiomException.Invalid($"Scope hint '{key}' may have at most {MaxAgentScopeValues} values.");
            }
        }

        // The excerpt hash is derived, not agent-supplied: the same proposal from the same evidence is a no-op.
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"axiom-agent-proposal-v1\n{locator}\n{statement}")));
        var now = time.GetUtcNow();
        var candidate = Build(
            principal,
            new CandidateInput(locator, hash, AgentProposalMethod, AgentProposalConfidence, input.SuggestedOwner, input.Kind ?? RecordKind.Decision, input.Title, statement, rationale, input.ScopeHints),
            now);
        var data = new
        {
            candidateId = candidate.Id,
            kind = candidate.Draft.Kind.ToString(),
            title = candidate.Draft.Title,
            source = candidate.Evidence.SourceLocator,
            suggestedOwner = candidate.Evidence.SuggestedOwner,
            proposedBy = principal.Subject,
        };
        return await store.AddAsync(candidate, [IntegrationEvent.Create(EventTypes.CandidateDecisionCreated, principal.OrganizationId, now, candidate.Id, data)], cancellationToken);
    }

    /// <summary>
    /// Approves or rejects a Proposed candidate, once. Allowed for approvers of the suggested owner team, DecisionOwner
    /// and PlatformAdmin; never for the subject that proposed it. A comment is required. Approving changes nothing in
    /// evaluation: only the record merged into the governance repository governs. The decision is written once (the state
    /// is the concurrency token) and appended to the outbox as a <c>candidate.decided</c> event.
    /// </summary>
    public async Task<CandidateDto> DecideAsync(AxiomPrincipal principal, string candidateId, bool approve, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        var candidate = await store.FindAsync(principal.OrganizationId, RequestLimits.Identifier(candidateId, "id"), cancellationToken)
            ?? throw AxiomException.NotFound($"Candidate '{candidateId}'");

        if (!CanDecide(principal, candidate))
        {
            throw AxiomException.Forbidden(string.IsNullOrWhiteSpace(candidate.Evidence.SuggestedOwner)
                ? "This candidate has no suggested owner; only a DecisionOwner or PlatformAdmin may decide it."
                : $"This candidate is routed to '{candidate.Evidence.SuggestedOwner}'; only its approvers, a DecisionOwner or a PlatformAdmin may decide it.");
        }

        if (string.Equals(principal.Subject, candidate.CreatedBy, StringComparison.Ordinal))
        {
            throw AxiomException.Forbidden("The party that proposed a candidate cannot decide it.");
        }

        if (candidate.State != CandidateState.Proposed)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "This candidate has already been decided.");
        }

        var text = RequestLimits.Text(comment, "comment", 4_000)?.Trim() ?? string.Empty;
        if (text.Length < MinimumCommentLength)
        {
            throw AxiomException.Invalid($"A decision needs a comment of at least {MinimumCommentLength} characters.");
        }

        var now = time.GetUtcNow();
        var decided = approve ? candidate.Approve(principal.Subject, text, now) : candidate.Reject(principal.Subject, text, now);
        var data = new { candidateId = candidate.Id, approved = approve, decidedBy = principal.Subject, suggestedOwner = candidate.Evidence.SuggestedOwner };
        var recorded = await store.UpdateAsync(
            decided, CandidateState.Proposed, [IntegrationEvent.Create(EventTypes.CandidateDecided, candidate.OrganizationId, now, candidate.Id, data)], cancellationToken);
        return recorded
            ? ToDto(decided)
            : throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "This candidate has already been decided.");
    }

    /// <summary>Whether the principal may decide the candidate. Does not consider self-approval.</summary>
    public static bool CanDecide(AxiomPrincipal principal, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(candidate);
        if (principal.IsInRole(Role.DecisionOwner) || principal.IsInRole(Role.PlatformAdmin))
        {
            return true;
        }

        var owner = candidate.Evidence.SuggestedOwner;
        return !string.IsNullOrWhiteSpace(owner)
            && principal.IsInRole(Role.Approver)
            && principal.Teams.Contains(owner, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CandidateDto> GetAsync(AxiomPrincipal principal, string candidateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        var candidate = await store.FindAsync(principal.OrganizationId, RequestLimits.Identifier(candidateId, "id"), cancellationToken)
            ?? throw AxiomException.NotFound($"Candidate '{candidateId}'");
        return ToDto(candidate);
    }

    public async Task<(ImmutableArray<CandidateDto> Items, int Total)> ListAsync(AxiomPrincipal principal, CandidateListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(query);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        var bounded = query with { Skip = Math.Max(0, query.Skip), Take = Math.Clamp(query.Take, 1, MaxPageSize), Owner = RequestLimits.OptionalIdentifier(query.Owner, "owner") };
        var (items, total) = await store.ListAsync(principal.OrganizationId, bounded, cancellationToken);
        return ([.. items.Select(ToDto)], total);
    }

    public static CandidateDto ToDto(Candidate c) => new(
        c.Id, c.State.ToString(), c.IsAuthoritative, c.Draft.Kind.ToString(), c.Draft.Title, c.Draft.Statement,
        c.Evidence.SourceLocator, c.Evidence.ExcerptHash, c.Evidence.ExtractionMethod, c.Evidence.Confidence, c.Evidence.SuggestedOwner,
        c.Draft.ScopeHints, CandidateRecordDraft.OwnersOf(c), CandidateRecordDraft.PathFor(c), CandidateRecordDraft.Render(c),
        c.CreatedAt, c.CreatedBy, c.DecidedAt, c.DecidedBy, c.DecisionComment, c.MergedRecordId,
        c.State switch
        {
            CandidateState.Approved => NextStepApproved,
            CandidateState.Rejected => NextStepRejected,
            CandidateState.MergedAsRecord => NextStepMerged,
            _ => NextStepProposed,
        });

    private static Candidate Build(AxiomPrincipal principal, CandidateInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!CandidateRecordDraft.IsSupportedKind(input.Kind))
        {
            throw AxiomException.Invalid($"A candidate cannot be a {input.Kind}. Use decision, standard, principle or goal; exceptions have their own request workflow.");
        }

        var source = RequestLimits.Identifier(input.SourceLocator, "sourceLocator");
        var hash = RequestLimits.Identifier(input.ExcerptHash, "excerptHash");
        var method = RequestLimits.Identifier(input.ExtractionMethod, "extractionMethod");
        if (double.IsNaN(input.Confidence) || input.Confidence is < 0 or > 1)
        {
            throw AxiomException.Invalid("'confidence' must be between 0 and 1.");
        }

        var title = RequestLimits.Text(input.Title, "title", 200)?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length < 3)
        {
            throw AxiomException.Invalid("'title' is required (at least 3 characters).");
        }

        var statement = RequestLimits.Text(input.Statement, "statement", 8_000)?.Trim();
        if (string.IsNullOrEmpty(statement))
        {
            throw AxiomException.Invalid("'statement' is required: a candidate without a normative statement proposes nothing.");
        }

        var rationale = RequestLimits.Text(input.Rationale, "rationale", 40_000)?.Trim() ?? string.Empty;
        var owner = RequestLimits.OptionalIdentifier(input.SuggestedOwner, "suggestedOwner");
        var candidate = new Candidate
        {
            Id = Candidate.IdFor(principal.OrganizationId, hash),
            OrganizationId = principal.OrganizationId,
            Evidence = new CandidateEvidence(source, hash, method, Math.Round(input.Confidence, 2), owner),
            Draft = new CandidateDraft(input.Kind, title, statement, rationale, Hints(input.ScopeHints)),
            State = CandidateState.Proposed,
            CreatedAt = now,
            CreatedBy = principal.Subject,
        };

        // The same rules that guard the Git repository vet the draft; a candidate that could never merge is not stored.
        var issues = GovernanceSetValidator.Validate([CandidateRecordDraft.ToRecord(candidate)]).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (issues.Count > 0)
        {
            throw AxiomException.Invalid("The candidate cannot become a valid record: " + string.Join(" ", issues.Select(i => i.Message)));
        }

        return candidate;
    }

    private static ImmutableSortedDictionary<string, ImmutableArray<string>> Hints(IReadOnlyDictionary<string, string[]>? hints)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in hints ?? new Dictionary<string, string[]>())
        {
            if (ExceptionScopeKeys.Dimension(key) is not { } dimension)
            {
                throw AxiomException.Invalid($"'{key}' is not a scope dimension. Use one of: {string.Join(", ", ExceptionScopeKeys.Names)}.");
            }

            var cleaned = (values ?? []).Select(v => RequestLimits.Identifier(v, key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (cleaned.IsEmpty || cleaned.Length > MaxScopeValues)
            {
                throw AxiomException.Invalid($"Scope hint '{key}' needs between 1 and {MaxScopeValues} values.");
            }

            if (dimension == ScopeDimension.Path)
            {
                cleaned = [.. cleaned.Select(GlobPattern.NormalizePath)];
            }

            builder[ExceptionScopeKeys.Canonical(key)] = cleaned;
        }

        return builder.ToImmutable();
    }
}
