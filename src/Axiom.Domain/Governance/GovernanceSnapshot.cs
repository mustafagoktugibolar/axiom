using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Axiom.Domain.Governance;

/// <summary>Where a record revision came from in the authoritative Git source (ADR-0001).</summary>
public sealed record SourceProvenance(string Repository, string CommitSha, string Path, string BlobSha);

/// <summary>A record revision together with its Git provenance and monotonic revision number.</summary>
public sealed record RecordRevision(GovernanceRecord Record, SourceProvenance Provenance, int Revision)
{
    public string Id => Record.Id;
}

/// <summary>
/// Content-addressed view of governance state at one source commit. Two snapshots with the same
/// identifier contain byte-identical records, which is what makes evaluations reproducible (P2, P7).
/// </summary>
public sealed class GovernanceSnapshot
{
    private readonly ImmutableSortedDictionary<string, RecordRevision> _byId;

    private GovernanceSnapshot(string id, string organizationId, string sourceCommit, ImmutableSortedDictionary<string, RecordRevision> byId)
    {
        Id = id;
        OrganizationId = organizationId;
        SourceCommit = sourceCommit;
        _byId = byId;
    }

    public string Id { get; }

    public string OrganizationId { get; }

    public string SourceCommit { get; }

    public IEnumerable<RecordRevision> Revisions => _byId.Values;

    public int Count => _byId.Count;

    public RecordRevision? Find(string recordId) => _byId.GetValueOrDefault(recordId);

    public static GovernanceSnapshot Create(string organizationId, string sourceCommit, IEnumerable<RecordRevision> revisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
        ArgumentNullException.ThrowIfNull(revisions);

        var byId = revisions.ToImmutableSortedDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        return new GovernanceSnapshot(ComputeId(organizationId, byId.Values.Select(r => (r.Id, r.Record.ContentHash))), organizationId, sourceCommit, byId);
    }

    /// <summary>
    /// The snapshot ID depends only on the organization and the (record ID, content hash) pairs, so
    /// rebuilding from the same source always yields the same ID regardless of commit metadata.
    /// </summary>
    public static string ComputeId(string organizationId, IEnumerable<(string Id, string ContentHash)> records)
    {
        var builder = new StringBuilder("axiom-snapshot-v1\n").Append(organizationId).Append('\n');
        foreach (var (id, hash) in records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            builder.Append(id).Append('=').Append(hash).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "gs_" + Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }
}
