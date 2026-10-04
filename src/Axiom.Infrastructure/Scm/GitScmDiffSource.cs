using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using LibGit2Sharp;
using PolicyChangeKind = Axiom.Domain.Policy.ChangeKind;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Scm;

/// <summary>
/// Diff source backed by Git mirrors. The clone URL comes from the System Graph (the repository
/// entity's <c>cloneUrls</c>). The mirror is only fetched into, never pushed to, and only commits
/// addressed by SHA are read, so the result does not depend on any working tree.
/// </summary>
public sealed class GitScmDiffSource(ISystemGraph graph, IOptionsMonitor<ScmOptions> options) : IScmDiffSource
{
    private const string RemoteName = "origin";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task<ScmDiff> GetDiffAsync(ScmDiffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue;
        var url = await ResolveCloneUrlAsync(request, settings, cancellationToken);
        var credential = CredentialFor(url, settings);
        var mirror = MirrorPath(settings, request.OrganizationId, url);

        var gate = Gates.GetOrAdd(mirror, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Read(request, settings, url, credential, mirror), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> ResolveCloneUrlAsync(ScmDiffRequest request, ScmOptions settings, CancellationToken cancellationToken)
    {
        var entity = await graph.ResolveRepositoryAsync(request.OrganizationId, request.Repository, cancellationToken);
        var node = entity is null ? null : await graph.FindAsync(request.OrganizationId, entity.Value, cancellationToken);
        var candidates = node is not null && node.Attributes.TryGetValue(RepositoryAttributes.CloneUrls, out var urls)
            ? RepositoryAttributes.SplitList(urls)
            : [];

        var allowed = candidates.FirstOrDefault(url => IsAllowed(url, settings));
        return allowed ?? throw new AxiomException(
            ErrorKind.Unavailable,
            ErrorCodes.ScmUnavailable,
            candidates.IsEmpty
                ? $"Repository '{request.Repository}' declares no clone URL in the System Graph, so its diff cannot be fetched."
                : $"None of the clone URLs of repository '{request.Repository}' points to a host Axiom is configured to fetch from.");
    }

    private static bool IsAllowed(string url, ScmOptions settings)
    {
        if (GovernanceSourceUrl.HasEmbeddedCredentials(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return settings.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || settings.Credentials.ContainsKey(uri.Host);
        }

        return settings.AllowLocalRepositories && IsLocal(url);
    }

    private static bool IsLocal(string url) =>
        url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || (!url.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(url));

    private static UsernamePasswordCredentials? CredentialFor(string url, ScmOptions settings) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && settings.Credentials.TryGetValue(uri.Host, out var credential)
        && !string.IsNullOrEmpty(credential.Token)
            ? new UsernamePasswordCredentials { Username = credential.Username, Password = credential.Token }
            : null;

    private static string MirrorPath(ScmOptions settings, string organizationId, string url)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{organizationId}\n{url}"));
        return Path.Combine(Path.GetFullPath(settings.CacheDirectory), Convert.ToHexStringLower(digest.AsSpan(0, 16)) + ".git");
    }

    private static ScmDiff Read(ScmDiffRequest request, ScmOptions settings, string url, UsernamePasswordCredentials? credential, string mirror)
    {
        if (!HasCommits(mirror, request.BaseSha, request.HeadSha))
        {
            Fetch(mirror, url, credential, request);
        }

        using var repository = new Repository(mirror);
        var head = repository.Lookup<Commit>(request.HeadSha)
            ?? throw Unavailable(request, $"commit '{request.HeadSha}' does not exist in the repository");
        var baseCommit = repository.Lookup<Commit>(request.BaseSha)
            ?? throw Unavailable(request, $"commit '{request.BaseSha}' does not exist in the repository");

        var compare = new CompareOptions { Similarity = SimilarityOptions.Default, ContextLines = 0 };
        using var changes = repository.Diff.Compare<TreeChanges>(baseCommit.Tree, head.Tree, compare);
        var selected = changes
            .Where(c => c.Mode != Mode.GitLink && c.OldMode != Mode.GitLink)
            .OrderBy(c => GlobPattern.NormalizePath(c.Path), StringComparer.Ordinal)
            .ToList();
        var total = selected.Count;
        var truncated = total > settings.MaxChangedFiles;
        var kept = truncated ? selected.Take(settings.MaxChangedFiles).ToList() : selected;

        var pathFilter = kept.SelectMany(c => new[] { c.Path, c.OldPath }).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
        using var patch = repository.Diff.Compare<Patch>(baseCommit.Tree, head.Tree, pathFilter, null, compare);

        var files = ImmutableArray.CreateBuilder<ChangedFile>(kept.Count);
        foreach (var change in kept)
        {
            files.Add(ToChangedFile(baseCommit, head, change, patch));
        }

        var treePaths = new List<string>();
        CollectPaths(head.Tree, treePaths);
        treePaths.Sort(StringComparer.Ordinal);
        return new ScmDiff(files.MoveToImmutable(), new GitRepositoryView(mirror, head.Sha, treePaths), truncated, total);
    }

    private static ChangedFile ToChangedFile(Commit baseCommit, Commit head, TreeEntryChanges change, Patch patch)
    {
        var path = GlobPattern.NormalizePath(change.Path);
        var kind = change.Status switch
        {
            LibGit2Sharp.ChangeKind.Added => PolicyChangeKind.Added,
            LibGit2Sharp.ChangeKind.Deleted => PolicyChangeKind.Deleted,
            LibGit2Sharp.ChangeKind.Renamed => PolicyChangeKind.Renamed,
            _ => PolicyChangeKind.Modified,
        };

        string? content = null, previous = null;
        if (kind != PolicyChangeKind.Deleted)
        {
            content = (head.Tree[change.Path]?.Target as Blob) is { } blob ? GitRepositoryView.ReadBlob(blob) : null;
        }

        if (kind is PolicyChangeKind.Modified or PolicyChangeKind.Deleted or PolicyChangeKind.Renamed)
        {
            previous = (baseCommit.Tree[change.OldPath]?.Target as Blob) is { } old ? GitRepositoryView.ReadBlob(old) : null;
        }

        var added = patch[change.Path] is { IsBinaryComparison: false } entry
            ? entry.AddedLines.Select(l => new AddedLine(l.LineNumber, l.Content.TrimEnd('\r', '\n'))).ToImmutableArray()
            : ImmutableArray<AddedLine>.Empty;

        return new ChangedFile(
            path,
            kind,
            kind == PolicyChangeKind.Renamed ? GlobPattern.NormalizePath(change.OldPath) : null,
            content,
            previous,
            added);
    }

    private static void CollectPaths(Tree tree, List<string> paths)
    {
        foreach (var entry in tree)
        {
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Tree:
                    CollectPaths((Tree)entry.Target, paths);
                    break;
                case TreeEntryTargetType.Blob:
                    paths.Add(GlobPattern.NormalizePath(entry.Path));
                    break;
            }
        }
    }

    private static bool HasCommits(string mirror, params string[] shas)
    {
        if (!Repository.IsValid(mirror))
        {
            return false;
        }

        using var repository = new Repository(mirror);
        return shas.All(sha => repository.Lookup<Commit>(sha) is not null);
    }

    private static void Fetch(string mirror, string url, UsernamePasswordCredentials? credential, ScmDiffRequest request)
    {
        if (!Repository.IsValid(mirror))
        {
            Directory.CreateDirectory(mirror);
            Repository.Init(mirror, isBare: true);
        }

        using var repository = new Repository(mirror);
        if (repository.Network.Remotes[RemoteName] is null)
        {
            repository.Network.Remotes.Add(RemoteName, url);
        }

        var fetch = new FetchOptions { TagFetchMode = TagFetchMode.None };
        if (credential is not null)
        {
            // The secret lives only in this callback; it is not written to the mirror's configuration.
            fetch.CredentialsProvider = (_, _, _) => credential;
        }

        try
        {
            // Branch heads and pull-request heads; a commit unreachable from either cannot be evaluated.
            Commands.Fetch(repository, RemoteName, ["+refs/heads/*:refs/remotes/origin/*", "+refs/pull/*/head:refs/pull/*/head", "+refs/pull/*/merge:refs/pull/*/merge"], fetch, null);
        }
        catch (LibGit2SharpException ex)
        {
            throw Unavailable(request, "the repository could not be fetched: " + ex.Message, ex);
        }
    }

    private static AxiomException Unavailable(ScmDiffRequest request, string reason, Exception? inner = null) =>
        new(ErrorKind.Unavailable, ErrorCodes.ScmUnavailable, $"The diff of '{request.Repository}' could not be read: {reason}.", retryable: true, innerException: inner);
}
