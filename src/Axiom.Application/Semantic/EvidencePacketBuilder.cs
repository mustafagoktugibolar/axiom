using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;

namespace Axiom.Application.Semantic;

/// <summary>
/// Builds the bounded evidence packet (task 7.2). Only effective, authoritative decisions, standards
/// and principles are included; only added lines of changed files are included; every text is cut to
/// its limit and scrubbed of obvious secrets before any provider sees it (requirements: minimize
/// source content sent to a provider).
/// </summary>
public static partial class EvidencePacketBuilder
{
    private const string Redacted = "[REDACTED]";

    public static EvidencePacket Build(
        string repository, string? task, IEnumerable<AppliedRecord> effective, IEnumerable<ChangedFile> changes, SemanticOptions options,
        IEnumerable<EvidenceComponent>? existingComponents = null)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(options);
        var truncated = false;

        var candidates = effective
            .Where(a => a.Record.Kind is RecordKind.Decision or RecordKind.Standard or RecordKind.Principle)
            .OrderBy(a => a.Revision.Id, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count > options.MaxRecords)
        {
            truncated = true;
        }

        var records = candidates.Take(options.MaxRecords).Select(a =>
        {
            var record = a.Record;
            var statement = Clip(record.Statement, options.MaxRecordChars, ref truncated);
            return new EvidenceRecord(
                record.Id, Clip(record.Title, 200, ref truncated), record.Kind.ToString(), record.ContentHash, statement,
                [.. record.Forbidden.Take(10).Select(f => Clip(f, 200, ref truncated))],
                [.. record.Preferred.Take(10).Select(f => Clip(f, 200, ref truncated))]);
        }).ToImmutableArray();

        var files = changes.Where(c => !c.AddedLines.IsDefaultOrEmpty).OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        if (files.Count > options.MaxFiles)
        {
            truncated = true;
        }

        var evidence = files.Take(options.MaxFiles).Select(file =>
        {
            var lines = file.AddedLines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
            if (lines.Count > options.MaxLinesPerFile)
            {
                truncated = true;
            }

            return new EvidenceChange(
                file.Path,
                [.. lines.Take(options.MaxLinesPerFile).Select(l => new EvidenceLine(l.Number, Scrub(Clip(l.Text.Trim(), options.MaxLineChars, ref truncated))))]);
        }).ToImmutableArray();

        var hints = (existingComponents ?? []).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        if (hints.Count > options.MaxExistingComponents)
        {
            truncated = true;
        }

        var components = hints.Take(options.MaxExistingComponents).Select(c => new EvidenceComponent(
            Scrub(Clip(c.Id, 200, ref truncated)),
            Scrub(Clip(c.Title, options.MaxComponentChars, ref truncated)),
            string.IsNullOrWhiteSpace(c.Description) ? null : Scrub(Clip(c.Description, options.MaxComponentChars, ref truncated)),
            [.. (c.Technologies.IsDefaultOrEmpty ? [] : c.Technologies).Take(8).Select(t => Scrub(Clip(t, 60, ref truncated)))])).ToImmutableArray();

        return new EvidencePacket(
            repository,
            string.IsNullOrWhiteSpace(task) ? null : Scrub(Clip(task.Trim(), options.MaxTaskChars, ref truncated)),
            records,
            evidence,
            truncated,
            components);
    }

    /// <summary>Removes values that look like credentials. It is a safety net, not a guarantee.</summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = PrivateKey().Replace(text, Redacted);
        result = KnownTokens().Replace(result, Redacted);
        result = Assignment().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Redacted);
        return result;
    }

    private static string Clip(string text, int limit, ref bool truncated)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        truncated = true;
        return string.Concat(text.AsSpan(0, limit), "…");
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|sk-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex KnownTokens();

    [GeneratedRegex(@"(?<key>(?:password|passwd|pwd|secret|api[_-]?key|token|connectionstring)\w*)(?<sep>\s*[:=]\s*)(?<value>""[^""]+""|'[^']+'|[^\s;,]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex Assignment();
}
