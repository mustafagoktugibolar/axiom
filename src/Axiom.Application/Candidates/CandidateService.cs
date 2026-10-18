using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Domain.Governance;

namespace Axiom.Application.Candidates;

/// <summary>
/// Intake and read side of the knowledge-onboarding candidates (R17). A candidate carries evidence, is routed to
/// a suggested owner and renders as a draft record, but it never governs: nothing here feeds resolution or
/// evaluation, and Axiom does not write to the governance repository (ADR-0001). Approval and rejection are a
/// later workflow; the states and the store are ready for it.
/// </summary>
public sealed class CandidateService(ICandidateStore store, TimeProvider time)
{
    public const int MaxBatch = 500;
    public const int MaxScopeValues = 50;
    public const int MaxPageSize = 200;

    public const string NextStepProposed =
        "Proposed. This candidate does not govern. A human owner reviews it, then commits the draft record to the governance repository and merges it as accepted.";
    public const string NextStepApproved = "Approved by a reviewer. It still governs nothing until the draft record is merged as accepted in the governance repository.";
    public const string NextStepRejected = "Rejected. Nothing was added to governance.";
    public const string NextStepMerged = "Merged. The accepted record in the governance repository governs; this candidate only documents where it came from.";

    /// <summary>
    /// Stores candidates generated from an external source. Idempotent by excerpt: knowledge already captured
    /// is reported as existing and left untouched, whatever state it has reached.
    /// </summary>
    public Task<CandidateIngestResult> IngestAsync(AxiomPrincipal principal, IReadOnlyCollection<CandidateInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
        return IngestCoreAsync(principal, inputs, cancellationToken);
    }

    /// <summary>
    /// Task 7.8: intake for candidates the platform itself derives while serving an evaluation. The caller was
    /// already authorized to evaluate; the result is a proposed, non-authoritative candidate, so the weaker
    /// right suffices. Same validation and idempotency as <see cref="IngestAsync"/>.
    /// </summary>
    public Task<CandidateIngestResult> IngestExtractedAsync(AxiomPrincipal principal, IReadOnlyCollection<CandidateInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.Evaluate);
        return IngestCoreAsync(principal, inputs, cancellationToken);
    }

    private async Task<CandidateIngestResult> IngestCoreAsync(AxiomPrincipal principal, IReadOnlyCollection<CandidateInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
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
