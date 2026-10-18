using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;

namespace Axiom.Application.Candidates;

public sealed record PostmortemImportResult(
    string Repository,
    string CommitSha,
    int FilesScanned,
    int StatementsFound,
    int Created,
    int Existing,
    ImmutableArray<AdrSkipped> Skipped,
    bool Truncated);

/// <summary>
/// Turns repository postmortem documents into invariant candidates (task 11.4): "X must ..." statements taken from
/// the root-cause, action-item and lessons sections, each with the file and section as its evidence locator. The
/// source is a repository tree read through the existing SCM adapter (or any <see cref="IRepositoryView"/>); there is
/// no new network path. Candidates only: nothing here creates a record.
/// </summary>
public sealed class PostmortemImportService(IScmTreeSource trees, CandidateService candidates)
{
    public const int MaxFiles = 300;
    public const int MaxFileBytes = 200_000;
    public const int MaxStatements = 300;
    public const int MaxSkippedReported = 100;

    public async Task<PostmortemImportResult> ImportRepositoryAsync(AxiomPrincipal principal, string repository, string? branch, string? pathPrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
        var name = RequestLimits.Identifier(repository, "repository");
        var prefix = RequestLimits.OptionalIdentifier(pathPrefix, "path");
        var tree = await trees.GetTreeAsync(new ScmTreeRequest(principal.OrganizationId, name, RequestLimits.OptionalIdentifier(branch, "branch")), cancellationToken);
        return await ImportAsync(principal, name, tree.CommitSha, tree.Tree, prefix, cancellationToken);
    }

    public async Task<PostmortemImportResult> ImportAsync(AxiomPrincipal principal, string repository, string commitSha, IRepositoryView files, string? pathPrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(files);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);

        var prefix = string.IsNullOrWhiteSpace(pathPrefix) ? null : GlobPattern.NormalizePath(pathPrefix).TrimEnd('/') + "/";
        var paths = files.Paths
            .Where(p => PostmortemParser.IsPostmortemPath(p) && (prefix is null || GlobPattern.NormalizePath(p).StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();
        var truncated = paths.Count > MaxFiles;
        var skipped = new List<AdrSkipped>();
        var inputs = new List<CandidateInput>();
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

            if (content.Length > MaxFileBytes)
            {
                skipped.Add(new AdrSkipped(path, "file is too large to read"));
                continue;
            }

            var document = PostmortemParser.Parse(content);
            if (document.Statements.IsEmpty)
            {
                skipped.Add(new AdrSkipped(path, "no root cause, action item or lesson states a rule"));
                continue;
            }

            foreach (var statement in document.Statements)
            {
                found++;
                var hash = CandidateText.Hash(statement.Statement);
                if (!seen.Add(hash))
                {
                    continue;
                }

                if (inputs.Count >= MaxStatements)
                {
                    truncated = true;
                    continue;
                }

                inputs.Add(ToInput(repository, commitSha, path, document.Title, statement, hash));
            }
        }

        var result = inputs.Count == 0
            ? new CandidateIngestResult(0, 0, [], [])
            : await candidates.IngestAsync(principal, inputs, cancellationToken);
        return new PostmortemImportResult(
            repository, commitSha, Math.Min(paths.Count, MaxFiles), found, result.Created, result.Existing,
            [.. skipped.Take(MaxSkippedReported)], truncated);
    }

    internal static CandidateInput ToInput(string repository, string commitSha, string path, string documentTitle, PostmortemStatement statement, string hash)
    {
        var section = statement.Section switch
        {
            PostmortemSection.RootCause => "root cause",
            PostmortemSection.ActionItems => "action items",
            _ => "lessons",
        };
        var rationale = $"From the {section} of the postmortem \"{(documentTitle.Length > 0 ? documentTitle : path)}\" (`{path}`, line {statement.Line}). "
            + (statement.Rewritten ? $"The postmortem says: \"{statement.Original}\" It was rewritten as a rule ({PostmortemParser.Method}); check that the rule says what the team means. " : "The sentence is quoted as written. ")
            + "A postmortem describes one incident; confirm the rule should hold generally before turning it into a record.";
        var name = EntityRef.TryParse(repository, out var reference) ? reference.Name : repository.Trim();
        return new CandidateInput(
            CandidateText.Locator(repository, path, $"#{PostmortemParser.SectionAnchor(statement)}", commitSha),
            hash,
            PostmortemParser.Method,
            statement.Confidence,
            null,
            RecordKind.Standard,
            CandidateText.Shorten(statement.Statement, 120),
            statement.Statement,
            rationale,
            new Dictionary<string, string[]> { ["repositories"] = [name] });
    }
}
