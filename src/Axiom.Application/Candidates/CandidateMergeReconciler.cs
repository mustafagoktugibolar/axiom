using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;

namespace Axiom.Application.Candidates;

/// <summary>
/// Marks approved candidates as merged once the published governance snapshot contains the record they became.
/// A candidate matches when an accepted record in the snapshot carries its excerpt hash as evidence, or uses its
/// draft record ID. It only documents provenance: the merged record governs, never the candidate.
/// The governance sync worker calls this after each synchronization.
/// </summary>
public sealed class CandidateMergeReconciler(ICandidateStore store, IGovernanceSnapshotProvider snapshots, TimeProvider time)
{
    /// <summary>Returns the IDs of the candidates that became <see cref="CandidateState.MergedAsRecord"/>.</summary>
    public async Task<IReadOnlyList<string>> ReconcileAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var snapshot = await snapshots.GetCurrentAsync(organizationId, cancellationToken);
        if (snapshot is null)
        {
            return [];
        }

        var accepted = snapshot.Revisions.Select(r => r.Record).Where(r => r.Status == LifecycleStatus.Accepted).ToList();
        var byExcerpt = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in accepted)
        {
            foreach (var evidence in record.Evidence)
            {
                byExcerpt.TryAdd(evidence.ExcerptHash, record.Id);
            }
        }

        var merged = new List<string>();
        var now = time.GetUtcNow();
        for (var skip = 0; ; skip += CandidateService.MaxPageSize)
        {
            var (items, total) = await store.ListAsync(organizationId, new CandidateListQuery(CandidateState.Approved, null, null, skip, CandidateService.MaxPageSize), cancellationToken);
            foreach (var candidate in items)
            {
                var recordId = byExcerpt.GetValueOrDefault(candidate.Evidence.ExcerptHash)
                    ?? accepted.Find(r => string.Equals(r.Id, CandidateRecordDraft.DraftId(candidate), StringComparison.Ordinal))?.Id;
                if (recordId is null)
                {
                    continue;
                }

                var data = new { candidateId = candidate.Id, recordId };
                if (await store.UpdateAsync(candidate.MarkMerged(recordId), CandidateState.Approved, [IntegrationEvent.Create(EventTypes.CandidateMerged, organizationId, now, candidate.Id, data)], cancellationToken))
                {
                    merged.Add(candidate.Id);
                }
            }

            if (skip + CandidateService.MaxPageSize >= total)
            {
                return merged;
            }
        }
    }
}
