using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;

namespace Axiom.Application.Coverage;

public enum CoverageStatus
{
    /// <summary>No accepted record applies to the repository: nothing governs changes to it.</summary>
    Uncovered,

    /// <summary>Records apply, but every one of them is past its review date.</summary>
    AllStale,

    Covered,
}

public enum CoverageFilter
{
    All,

    /// <summary>Repositories that are <see cref="CoverageStatus.Uncovered"/> or <see cref="CoverageStatus.AllStale"/>.</summary>
    Gaps,
    Uncovered,
    AllStale,
    Covered,
}

public sealed record RepositoryCoverage(
    string Repository,
    CoverageStatus Status,
    int ApplicableRecords,
    int BoundRules,
    int StaleRecords,
    int Gaps,
    ImmutableArray<string> RecordIds);

/// <param name="TotalRepositories">All repositories in the System Graph, not only this page.</param>
/// <param name="Summary">Counts over the repositories examined on this page, before the filter is applied.</param>
public sealed record CoverageReport(
    string SnapshotId,
    string CatalogVersion,
    int TotalRepositories,
    int Skip,
    int Take,
    bool HasMore,
    CoverageSummary Summary,
    ImmutableArray<RepositoryCoverage> Items);

public sealed record CoverageSummary(int Examined, int Uncovered, int AllStale, int Covered, int ApplicableRecords, int BoundRules);

/// <summary>
/// How much governance reaches each repository (task 11.8). For every repository of the System Graph it
/// resolves the records that apply with the same scope resolution evaluations use, then counts the applicable
/// accepted records, the executable rules bound to them, and how many are past their review date. Read-only:
/// it measures governance, it never changes it. Paged, because each repository costs one scope resolution.
/// </summary>
public sealed class GovernanceCoverageService(
    IRepositoryDirectory repositories, ChangeScopeResolver scopes, IGovernanceSnapshotProvider snapshots, TimeProvider time)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 50;

    public async Task<CoverageReport> GetAsync(AxiomPrincipal principal, CoverageFilter filter, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        Authorizer.Demand(principal, AccessRight.ReadCatalog);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPageSize);

        var snapshot = await snapshots.GetCurrentAsync(principal.OrganizationId, cancellationToken)
            ?? throw AxiomException.Unavailable(ErrorCodes.NoPublishedSnapshot, "No governance snapshot has been published for this organization.");
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var revisions = snapshot.Revisions.ToList();

        var (page, total) = await repositories.ListRepositoriesAsync(principal.OrganizationId, skip, take, cancellationToken);
        var covered = ImmutableArray.CreateBuilder<RepositoryCoverage>(page.Length);
        var catalogVersion = string.Empty;
        foreach (var repository in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = await scopes.ResolveAsync(new ChangeScopeRequest(principal.OrganizationId, repository.ToString(), null, null), cancellationToken);
            catalogVersion = change.CatalogVersion;
            var resolution = DecisionResolver.Resolve(new ResolutionInput(revisions, change.Scope, now, HardGate: false));
            covered.Add(Measure(repository.ToString(), resolution, change.Gaps.Length, today));
        }

        var summary = new CoverageSummary(
            covered.Count,
            covered.Count(c => c.Status == CoverageStatus.Uncovered),
            covered.Count(c => c.Status == CoverageStatus.AllStale),
            covered.Count(c => c.Status == CoverageStatus.Covered),
            covered.Sum(c => c.ApplicableRecords),
            covered.Sum(c => c.BoundRules));
        var items = covered.Where(c => Matches(filter, c.Status)).ToImmutableArray();
        return new CoverageReport(snapshot.Id, catalogVersion, total, skip, take, skip + page.Length < total, summary, items);
    }

    internal static RepositoryCoverage Measure(string repository, ResolutionResult resolution, int gaps, DateOnly today)
    {
        var applicable = resolution.Applicable;
        var stale = applicable.Count(a => a.Record.Validity.IsReviewOverdueOn(today));
        var status = applicable.IsEmpty ? CoverageStatus.Uncovered : stale == applicable.Length ? CoverageStatus.AllStale : CoverageStatus.Covered;
        return new RepositoryCoverage(
            repository,
            status,
            applicable.Length,
            applicable.Sum(a => a.Record.Enforcement.Rules.IsDefaultOrEmpty ? 0 : a.Record.Enforcement.Rules.Length),
            stale,
            gaps,
            [.. applicable.Select(a => a.Revision.Id).Order(StringComparer.Ordinal)]);
    }

    private static bool Matches(CoverageFilter filter, CoverageStatus status) => filter switch
    {
        CoverageFilter.Gaps => status != CoverageStatus.Covered,
        CoverageFilter.Uncovered => status == CoverageStatus.Uncovered,
        CoverageFilter.AllStale => status == CoverageStatus.AllStale,
        CoverageFilter.Covered => status == CoverageStatus.Covered,
        _ => true,
    };
}
