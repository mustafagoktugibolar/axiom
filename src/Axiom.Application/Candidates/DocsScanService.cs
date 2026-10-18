using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;

namespace Axiom.Application.Candidates;

/// <param name="StatementsFound">Decision-like statements matched, before duplicates are folded.</param>
/// <param name="Created">Candidates newly stored.</param>
/// <param name="Existing">Candidates whose excerpt was captured earlier; left untouched.</param>
/// <param name="Truncated">True when file or statement limits cut the scan short.</param>
public sealed record DocsScanResult(
    string Repository,
    string CommitSha,
    int FilesScanned,
    int StatementsFound,
    int Created,
    int Existing,
    ImmutableArray<AdrSkipped> Skipped,
    bool Truncated);

/// <summary>
/// Scans repository documentation for decision-like statements (task 11.2) and stores each as a proposed
/// candidate. Extraction is the deterministic <see cref="DocsScanner"/>; every candidate names its method and a
/// confidence, and is identified by the hash of the sentence, so rescanning is idempotent. The tree is read through
/// the SCM adapter, so the allowed hosts and credentials are those of diff evaluation. Nothing here can create a record.
/// </summary>
public sealed class DocsScanService(IScmTreeSource trees, CandidateService candidates)
{
    public const int MaxFiles = 300;
    public const int MaxStatements = 300;
    public const int MaxSkippedReported = 100;

    public async Task<DocsScanResult> ScanRepositoryAsync(AxiomPrincipal principal, string repository, string? branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
        var name = RequestLimits.Identifier(repository, "repository");
        var tree = await trees.GetTreeAsync(new ScmTreeRequest(principal.OrganizationId, name, RequestLimits.OptionalIdentifier(branch, "branch")), cancellationToken);
        return await ScanAsync(principal, name, tree.CommitSha, tree.Tree, cancellationToken);
    }

    public async Task<DocsScanResult> ScanAsync(AxiomPrincipal principal, string repository, string commitSha, IRepositoryView files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(files);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);

        var paths = files.Paths.Where(DocsScanner.IsDocPath).Order(StringComparer.Ordinal).ToList();
        var truncated = paths.Count > MaxFiles;
        var skipped = new List<AdrSkipped>();
        var statements = new List<(DocStatement Statement, string Hash)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = 0;
        foreach (var path in paths.Take(MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = files.Read(path);
            if (content is null)
            {
                skipped.Add(new AdrSkipped(path, "binary or unreadable"));
                continue;
            }

            if (content.Length > DocsScanner.MaxFileBytes)
            {
                skipped.Add(new AdrSkipped(path, "file is too large to scan"));
                continue;
            }

            foreach (var statement in DocsScanner.Scan(path, content))
            {
                found++;
                var hash = CandidateText.Hash(statement.Sentence);
                if (!seen.Add(hash))
                {
                    continue;
                }

                if (statements.Count >= MaxStatements)
                {
                    truncated = true;
                    continue;
                }

                statements.Add((statement, hash));
            }
        }

        var inputs = statements.Select(s => ToInput(repository, commitSha, s.Statement, s.Hash)).ToList();
        var result = inputs.Count == 0
            ? new CandidateIngestResult(0, 0, [], [])
            : await candidates.IngestAsync(principal, inputs, cancellationToken);
        return new DocsScanResult(
            repository, commitSha, Math.Min(paths.Count, MaxFiles), found, result.Created, result.Existing,
            [.. skipped.Take(MaxSkippedReported)], truncated);
    }

    internal static CandidateInput ToInput(string repository, string commitSha, DocStatement statement, string hash)
    {
        var kind = statement.Method == DocsScanner.RuleMethod ? RecordKind.Standard : RecordKind.Decision;
        var rationale = $"Found in `{statement.Path}` (line {statement.Line}"
            + (statement.Heading.Length > 0 ? $", under \"{statement.Heading}\"" : string.Empty)
            + $") by phrase \"{statement.Phrase}\" ({statement.Method}). This is prose matched by a fixed phrase list, not a recorded decision: "
            + "check that it is still true and that it is meant as a rule before turning it into a record.";
        var name = EntityRef.TryParse(repository, out var reference) ? reference.Name : repository.Trim();
        return new CandidateInput(
            CandidateText.Locator(repository, statement.Path, $"#L{statement.Line}", commitSha),
            hash,
            statement.Method,
            statement.Confidence,
            null,
            kind,
            CandidateText.Shorten(statement.Sentence, 120),
            statement.Sentence,
            rationale,
            new Dictionary<string, string[]> { ["repositories"] = [name] });
    }
}
