using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;

namespace Axiom.Application.Candidates;

public sealed record AdrSkipped(string Path, string Reason);

/// <param name="Created">Candidates newly stored.</param>
/// <param name="Existing">Candidates whose excerpt was already captured earlier; left untouched.</param>
/// <param name="FilesTruncated">True when the repository holds more ADR files than one import reads.</param>
public sealed record AdrImportResult(
    string Repository,
    string CommitSha,
    int FilesScanned,
    int Created,
    int Existing,
    ImmutableArray<AdrSkipped> Skipped,
    bool FilesTruncated);

/// <summary>
/// Turns the ADR markdown files of a repository into candidates (task 11.1). Imported ADRs are knowledge,
/// not governance: they are stored as proposed candidates with evidence, and only a human-approved record
/// merged into the governance repository ever governs. Re-importing is idempotent because a candidate is
/// identified by the hash of the decision text it was extracted from.
/// </summary>
public sealed partial class AdrImportService(IScmTreeSource trees, CandidateService candidates)
{
    public const int MaxFiles = 500;
    public const int MaxStatementLength = 4_000;
    public const int MaxSkippedReported = 100;

    public async Task<AdrImportResult> ImportRepositoryAsync(AxiomPrincipal principal, string repository, string? branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
        var name = RequestLimits.Identifier(repository, "repository");

        // Read through the SCM adapter: same allowed hosts, credentials and fetch rules as diff evaluation.
        var tree = await trees.GetTreeAsync(new ScmTreeRequest(principal.OrganizationId, name, RequestLimits.OptionalIdentifier(branch, "branch")), cancellationToken);
        return await ImportAsync(principal, name, tree.CommitSha, tree.Tree, cancellationToken);
    }

    /// <summary>Imports ADRs from any read-only file view (a repository checkout, a governance-source tree, a local path).</summary>
    public async Task<AdrImportResult> ImportAsync(AxiomPrincipal principal, string repository, string commitSha, IRepositoryView files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(files);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);

        var paths = files.Paths.Where(AdrParser.IsAdrPath).Order(StringComparer.Ordinal).ToList();
        var truncated = paths.Count > MaxFiles;
        var skipped = new List<AdrSkipped>();
        var inputs = new List<CandidateInput>();
        foreach (var path in paths.Take(MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = files.Read(path);
            if (content is null)
            {
                skipped.Add(new AdrSkipped(path, "binary or unreadable"));
                continue;
            }

            var document = AdrParser.Parse(path, content, out var reason);
            if (document is null)
            {
                skipped.Add(new AdrSkipped(path, reason ?? "not an ADR"));
                continue;
            }

            if (document.Status is AdrStatus.Rejected or AdrStatus.Deprecated or AdrStatus.Superseded)
            {
                skipped.Add(new AdrSkipped(path, $"status is {document.Status.ToString().ToLowerInvariant()}; it no longer describes a decision to adopt"));
                continue;
            }

            inputs.Add(ToInput(repository, commitSha, path, document));
        }

        var result = inputs.Count == 0
            ? new CandidateIngestResult(0, 0, [], [])
            : await candidates.IngestAsync(principal, inputs, cancellationToken);
        return new AdrImportResult(
            repository, commitSha, paths.Count > MaxFiles ? MaxFiles : paths.Count, result.Created, result.Existing,
            [.. skipped.Take(MaxSkippedReported)], truncated);
    }

    internal static CandidateInput ToInput(string repository, string commitSha, string path, AdrDocument adr)
    {
        var statement = Collapse(adr.Decision);
        if (statement.Length > MaxStatementLength)
        {
            statement = statement[..(MaxStatementLength - 1)] + "…";
        }

        var rationale = new StringBuilder();
        if (adr.Context.Length > 0)
        {
            rationale.AppendLine("## Context").AppendLine().AppendLine(adr.Context.Trim()).AppendLine();
        }

        if (adr.Consequences.Length > 0)
        {
            rationale.AppendLine("## Consequences").AppendLine().AppendLine(adr.Consequences.Trim()).AppendLine();
        }

        rationale.Append("Imported from the ADR `").Append(path).Append("` (status: ").Append(string.IsNullOrEmpty(adr.RawStatus) ? "not stated" : adr.RawStatus).Append(").");

        var scope = new Dictionary<string, string[]> { ["repositories"] = [RepositoryName(repository)] };
        var shortSha = commitSha.Length > 12 ? commitSha[..12] : commitSha;
        return new CandidateInput(
            $"{repository}:{path}@{shortSha}",
            ExcerptHash(adr),
            AdrParser.MethodFor(adr.Format),
            Confidence(adr),
            adr.Decider,
            RecordKind.Decision,
            adr.Title,
            statement,
            rationale.ToString(),
            scope);
    }

    /// <summary>Hash of what the candidate proposes (title and decision, whitespace-normalized), so edits elsewhere in the file do not create duplicates.</summary>
    internal static string ExcerptHash(AdrDocument adr) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Collapse(adr.Title) + "\n" + Collapse(adr.Decision))));

    /// <summary>Accepted ADRs are the strongest evidence; a stated consequence section shows the decision was thought through.</summary>
    internal static double Confidence(AdrDocument adr)
    {
        var baseline = adr.Status switch { AdrStatus.Accepted => 0.85, AdrStatus.Proposed => 0.6, _ => 0.5 };
        return Math.Min(0.95, baseline + (adr.Consequences.Length > 0 ? 0.05 : 0));
    }

    private static string RepositoryName(string repository) =>
        EntityRef.TryParse(repository, out var reference) ? reference.Name : repository.Trim();

    private static string Collapse(string text) => Whitespace().Replace(text.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
