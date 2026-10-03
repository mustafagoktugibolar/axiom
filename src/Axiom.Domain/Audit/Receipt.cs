using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Axiom.Domain.Evaluation;

namespace Axiom.Domain.Audit;

/// <summary>
/// Immutable, content-addressed evidence of an evaluation (R16, design.md §4.8). The digest covers the
/// canonical payload; receipts of one organization form a hash chain so removal or alteration of any
/// receipt is detectable (tamper-evident audit trail).
/// </summary>
public sealed record Receipt(
    string Id,
    string OrganizationId,
    string EvaluationId,
    EvaluationStage Stage,
    Verdict Verdict,
    string Repository,
    string? CommitSha,
    string SnapshotId,
    DateTimeOffset IssuedAt,
    string Payload,
    string Digest,
    string PreviousChainDigest,
    string ChainDigest);

public static class ReceiptFactory
{
    public const string PayloadVersion = "axiom-receipt-v1";

    /// <summary>The chain digest that precedes an organization's first receipt.</summary>
    public static string GenesisDigest { get; } = new('0', 64);

    public static Receipt Create(EvaluationRecord evaluation, string previousChainDigest)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousChainDigest);

        var payload = CanonicalPayload(evaluation);
        var digest = Sha256(payload);
        return new Receipt(
            "rc_" + digest[..24],
            evaluation.OrganizationId,
            evaluation.Id,
            evaluation.Stage,
            evaluation.Verdict,
            evaluation.Scm.Repository,
            evaluation.Scm.CommitSha,
            evaluation.SnapshotId,
            evaluation.CreatedAt,
            payload,
            digest,
            previousChainDigest,
            Chain(previousChainDigest, digest));
    }

    public static string Chain(string previousChainDigest, string digest) => Sha256(previousChainDigest + "\n" + digest);

    /// <summary>True when the stored digest matches the payload and the chain link is intact.</summary>
    public static bool Verify(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return string.Equals(Sha256(receipt.Payload), receipt.Digest, StringComparison.Ordinal)
            && string.Equals(Chain(receipt.PreviousChainDigest, receipt.Digest), receipt.ChainDigest, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonical JSON: fixed property order, sorted collections, no insignificant whitespace. Free text
    /// (task, messages) is included as a hash, so the receipt proves what was evaluated without
    /// duplicating potentially sensitive content.
    /// </summary>
    public static string CanonicalPayload(EvaluationRecord e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("version", PayloadVersion);
            w.WriteString("evaluationId", e.Id);
            w.WriteString("organizationId", e.OrganizationId);
            w.WriteString("stage", e.Stage.ToString());
            w.WriteString("createdAt", e.CreatedAt.ToUniversalTime().ToString("O"));
            w.WriteString("requestFingerprint", e.RequestFingerprint);

            w.WriteStartObject("actor");
            w.WriteString("subject", e.Actor.Subject);
            WriteNullable(w, "harnessType", e.Actor.HarnessType);
            WriteNullable(w, "harnessSessionId", e.Actor.HarnessSessionId);
            w.WriteEndObject();

            w.WriteStartObject("scm");
            w.WriteString("repository", e.Scm.Repository);
            WriteNullable(w, "ref", e.Scm.Ref);
            WriteNullable(w, "commitSha", e.Scm.CommitSha);
            WriteNullable(w, "baseSha", e.Scm.BaseSha);
            WriteNullable(w, "pullRequest", e.Scm.PullRequest);
            w.WriteEndObject();

            WriteNullable(w, "taskHash", e.Task is null ? null : Sha256(e.Task));
            WriteNullable(w, "parentEvaluationId", e.ParentEvaluationId);
            WriteNullable(w, "designId", e.DesignId);
            WriteNullable(w, "designHash", e.DesignHash);

            w.WriteStartObject("governance");
            w.WriteString("snapshotId", e.SnapshotId);
            w.WriteString("sourceCommit", e.SourceCommit);
            w.WriteString("catalogVersion", e.CatalogVersion);
            w.WriteEndObject();

            w.WriteStartArray("appliedRecords");
            foreach (var r in e.AppliedRecords.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteNumber("revision", r.Revision);
                w.WriteString("contentHash", r.ContentHash);
                w.WriteString("importance", r.Importance.ToString());
                WriteStrings(w, "waivedBy", r.WaivedBy.Concat(r.PartiallyWaivedBy));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("appliedExceptions");
            foreach (var x in e.AppliedExceptions.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("id", x.Id);
                w.WriteNumber("revision", x.Revision);
                w.WriteString("contentHash", x.ContentHash);
                w.WriteString("expiresAt", x.ExpiresAt.ToUniversalTime().ToString("O"));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("policyRuns");
            foreach (var p in e.PolicyRuns.OrderBy(p => p.RecordId, StringComparer.Ordinal).ThenBy(p => p.RuleId, StringComparer.Ordinal).ThenBy(p => p.RuleHash, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("recordId", p.RecordId);
                w.WriteString("ruleId", p.RuleId);
                w.WriteString("ruleVersion", p.RuleVersion);
                w.WriteString("ruleHash", p.RuleHash);
                w.WriteString("outcome", p.Outcome.ToString());
                w.WriteNumber("violations", p.Violations.Length);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartObject("semantic");
            w.WriteString("status", e.SemanticCoverage.Status.ToString());
            WriteNullable(w, "provider", e.SemanticCoverage.Provider);
            WriteNullable(w, "model", e.SemanticCoverage.Model);
            w.WriteEndObject();

            w.WriteStartArray("findings");
            foreach (var f in e.Findings
                .OrderBy(f => f.Code, StringComparer.Ordinal)
                .ThenBy(f => string.Join(",", f.GovernanceIds), StringComparer.Ordinal)
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Message, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("code", f.Code);
                w.WriteString("severity", f.Severity.ToString());
                w.WriteString("source", f.Source.ToString());
                WriteStrings(w, "governanceIds", f.GovernanceIds);
                WriteNullable(w, "ruleId", f.RuleId);
                WriteNullable(w, "ruleVersion", f.RuleVersion);
                WriteNullable(w, "path", f.Path);
                if (f.Line is { } line)
                {
                    w.WriteNumber("line", line);
                }

                WriteNullable(w, "waivedBy", f.WaivedBy);
                w.WriteString("messageHash", Sha256(f.Message));
                WriteStrings(w, "evidence", f.Evidence.Select(ev => $"{ev.Source}|{ev.Id}|{ev.Revision}|{ev.Locator}|{ev.Hash}"));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteBoolean("significantChange", e.Significance.IsSignificant);
            WriteStrings(w, "significanceTriggers", e.Significance.Triggers.Select(t => t.Code));
            w.WriteString("verdict", VerdictLattice.ToContract(e.Verdict));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
