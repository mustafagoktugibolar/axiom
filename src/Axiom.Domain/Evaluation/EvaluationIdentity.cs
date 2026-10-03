using System.Security.Cryptography;
using System.Text;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Evaluation;

/// <summary>
/// Deterministic identifiers for evaluations. Two requests get the same evaluation ID exactly when
/// nothing that could change the answer differs: request, governance snapshot, catalog version, rule
/// catalog, and the validity epoch (design.md §17: never reuse across those boundaries).
/// </summary>
public static class EvaluationIdentity
{
    public static string Fingerprint(params string?[] parts)
    {
        var builder = new StringBuilder("axiom-request-v1");
        foreach (var part in parts)
        {
            // Length-prefix each part so that ("ab","c") and ("a","bc") cannot collide.
            builder.Append('\n').Append(part is null ? "-1" : part.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(part);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static string EvaluationId(string fingerprint, string snapshotId, string catalogVersion, string engineVersion, DateTimeOffset validityEpoch) =>
        "ev_" + Fingerprint(fingerprint, snapshotId, catalogVersion, engineVersion, validityEpoch.ToUniversalTime().ToString("O"))[..32];

    /// <summary>
    /// The latest instant, not after <paramref name="now"/>, at which any record or exception in the
    /// snapshot started or stopped applying. It changes exactly when a validity boundary is crossed.
    /// </summary>
    public static DateTimeOffset ValidityEpoch(GovernanceSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var epoch = DateTimeOffset.MinValue;
        foreach (var boundary in snapshot.Revisions.SelectMany(r => Boundaries(r.Record)))
        {
            if (boundary <= now && boundary > epoch)
            {
                epoch = boundary;
            }
        }

        return epoch;
    }

    private static IEnumerable<DateTimeOffset> Boundaries(GovernanceRecord record)
    {
        if (record.Exception is { } terms)
        {
            yield return terms.StartsAt;
            yield return terms.ExpiresAt;
        }

        if (record.Validity.EffectiveFrom is { } from)
        {
            yield return new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        if (record.Validity.EffectiveUntil is { } until)
        {
            // Effective through the end of that day, so the boundary is the following midnight.
            yield return new DateTimeOffset(until.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
    }
}
