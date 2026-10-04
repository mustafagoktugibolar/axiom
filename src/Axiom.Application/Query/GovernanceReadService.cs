using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;

namespace Axiom.Application.Query;

public sealed record RuleBindingDto(string RuleId, string Mode, ImmutableSortedDictionary<string, string> Parameters);

public sealed record RelationDto(string Kind, string TargetId);

public sealed record RevisionDto(int Revision, string ContentHash, string Status, string CommitSha, DateTimeOffset CommittedAt, string Author, string Path);

/// <summary>A governance record as REST and MCP show it. The rationale body is included only on request.</summary>
public sealed record GovernanceRecordDto(
    string Id,
    string Kind,
    string Title,
    string Status,
    bool Authoritative,
    int Revision,
    ImmutableArray<string> Owners,
    ImmutableArray<string> Tags,
    string AuthorityLevel,
    bool Exemptable,
    ImmutableSortedDictionary<string, ImmutableArray<string>> Scope,
    string DefaultVerdict,
    ImmutableArray<RuleBindingDto> Rules,
    string Statement,
    ImmutableArray<string> Forbidden,
    ImmutableArray<string> Preferred,
    ImmutableArray<RelationDto> Relations,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveUntil,
    DateOnly? ReviewAfter,
    string ContentHash,
    string SourcePath,
    string SourceCommit,
    string? Rationale)
{
    public static GovernanceRecordDto From(RecordRevision revision, DateOnly today, bool includeRationale)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var r = revision.Record;
        return new GovernanceRecordDto(
            r.Id, r.Kind.ToString(), r.Title, r.Status.ToString(), r.IsAuthoritativeOn(today), revision.Revision, r.Owners, r.Tags,
            r.Authority.Level.ToString(), r.Authority.Exemptable,
            r.Scope.RestrictedDimensions.ToImmutableSortedDictionary(d => char.ToLowerInvariant(d.ToString()[0]) + d.ToString()[1..], d => r.Scope[d].ToImmutableArray(), StringComparer.Ordinal),
            r.Enforcement.DefaultVerdict.ToString(),
            [.. (r.Enforcement.Rules.IsDefault ? [] : r.Enforcement.Rules).Select(b => new RuleBindingDto(b.RuleId, b.Mode.ToString(), b.Parameters))],
            r.Statement, r.Forbidden, r.Preferred, [.. r.Relations.Select(x => new RelationDto(x.Kind.ToString(), x.TargetId))],
            r.Validity.EffectiveFrom, r.Validity.EffectiveUntil, r.Validity.ReviewAfter,
            r.ContentHash, revision.Provenance.Path, revision.Provenance.CommitSha,
            includeRationale ? r.Body : null);
    }
}

public sealed record GovernanceDetailDto(
    GovernanceRecordDto Record,
    string SourceRepository,
    ImmutableArray<RevisionDto> History,
    ImmutableArray<RelationDto> IncomingRelations);

/// <summary>
/// Read access to governance records for REST and MCP (R19). Historical and candidate records are
/// returned and flagged; only <see cref="GovernanceRecordDto.Authoritative"/> ones govern.
/// </summary>
public sealed class GovernanceReadService(IGovernanceQueries queries, TimeProvider time)
{
    public const int MaxPageSize = 200;

    public Task<GovernanceSearchResult> SearchAsync(AxiomPrincipal principal, GovernanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(query);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        return queries.SearchAsync(principal.OrganizationId, query with { Skip = Math.Max(0, query.Skip), Take = Math.Clamp(query.Take, 1, MaxPageSize) }, cancellationToken);
    }

    public async Task<GovernanceDetailDto> GetAsync(AxiomPrincipal principal, string recordId, bool includeRationale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        if (string.IsNullOrWhiteSpace(recordId) || recordId.Length > 128)
        {
            throw AxiomException.Invalid("'id' is not a valid governance record ID.");
        }

        var detail = await queries.GetAsync(principal.OrganizationId, recordId.Trim(), cancellationToken)
            ?? throw AxiomException.NotFound($"Governance record '{recordId}'");
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return new GovernanceDetailDto(
            GovernanceRecordDto.From(detail.Current, today, includeRationale),
            detail.SourceRepository,
            [.. detail.History.Select(h => new RevisionDto(h.Revision, h.ContentHash, h.Status.ToString(), h.CommitSha, h.CommittedAt, h.Author, h.Path))],
            [.. detail.IncomingRelations.Select(x => new RelationDto(x.Kind.ToString(), x.TargetId))]);
    }

    /// <summary>The exact revision a past evaluation applied (P7), for context bundles and explanations.</summary>
    internal async Task<GovernanceRecordDto?> GetRevisionAsync(string organizationId, string recordId, int revision, bool includeRationale, CancellationToken cancellationToken)
    {
        var found = await queries.GetRevisionAsync(organizationId, recordId, revision, cancellationToken);
        return found is null ? null : GovernanceRecordDto.From(found, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), includeRationale);
    }
}
