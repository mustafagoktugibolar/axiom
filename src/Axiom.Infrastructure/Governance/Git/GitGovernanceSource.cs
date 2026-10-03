using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Governance.Git;

/// <summary>
/// Reads the authoritative governance repository (ADR-0001) through a local bare mirror per source.
/// The mirror is only ever fetched into; nothing is pushed, so the source is never written to.
/// </summary>
public sealed class GitGovernanceSource(IOptionsMonitor<GovernanceOptions> options, IGovernanceSourceCredentials credentials)
    : IGovernanceSource, IGovernanceCommitSource
{
    private const string RemoteName = "origin";

    // One gate per mirror directory: libgit2 repositories are not safe for concurrent fetches.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public async Task<SourceCommit> FetchHeadAsync(GovernanceSourceConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var credential = await ResolveCredentialAsync(config, cancellationToken);
        return await RunAsync(config, cancellationToken, mirror =>
        {
            Fetch(mirror, config, credential);
            using var repository = new Repository(mirror);
            var reference = repository.Refs[BranchRef(config)]?.ResolveToDirectReference()
                ?? throw new InvalidOperationException($"Branch '{config.Branch}' does not exist in the governance repository of organization '{config.OrganizationId}'.");
            var tip = reference.Target.Peel<Commit>();
            return ToSourceCommit(tip);
        });
    }

    public Task<IReadOnlyList<SourceFile>> ReadTreeAsync(GovernanceSourceConfig config, string commitSha, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<SourceFile>>(config, commitSha, cancellationToken, (_, commit) =>
        {
            var root = Root(config);
            var files = new List<SourceFile>();
            var start = root.Length == 0 ? commit.Tree : commit[root]?.Target as Tree;
            if (start is null)
            {
                return files;
            }

            var pending = new Stack<Tree>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                foreach (var entry in pending.Pop())
                {
                    if (entry.TargetType == TreeEntryTargetType.Tree)
                    {
                        pending.Push((Tree)entry.Target);
                    }
                    else if (entry.TargetType == TreeEntryTargetType.Blob && ToSourceFile(entry) is { } file)
                    {
                        files.Add(file);
                    }
                }
            }

            files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
            return files;
        });

    public Task<IReadOnlyList<SourceFileVersion>> ReadHistoryAsync(GovernanceSourceConfig config, string commitSha, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = GlobPattern.NormalizePath(path);
        return ReadAsync<IReadOnlyList<SourceFileVersion>>(config, commitSha, cancellationToken, (_, commit) =>
        {
            var versions = new List<SourceFileVersion>();
            if (!IsUnderRoot(normalized, Root(config)))
            {
                return versions;
            }

            string? previousBlob = null;
            foreach (var ancestor in FirstParentChain(commit).Reverse())
            {
                var entry = ancestor[normalized];
                var file = entry is { TargetType: TreeEntryTargetType.Blob } ? ToSourceFile(entry) : null;
                if (file is not null && !string.Equals(file.BlobSha, previousBlob, StringComparison.Ordinal))
                {
                    versions.Add(new SourceFileVersion(ToSourceCommit(ancestor), file.Path, file.BlobSha, file.Content));
                }

                previousBlob = file?.BlobSha;
            }

            return versions;
        });
    }

    public Task<IReadOnlyList<string>?> ChangedPathsAsync(GovernanceSourceConfig config, string fromSha, string toSha, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<string>?>(config, toSha, cancellationToken, (repository, target) =>
        {
            var origin = repository.Lookup<Commit>(fromSha);
            if (origin is null)
            {
                return null;
            }

            if (origin.Sha != target.Sha && repository.ObjectDatabase.FindMergeBase(origin, target)?.Sha != origin.Sha)
            {
                return null;
            }

            var root = Root(config);
            using var changes = repository.Diff.Compare<TreeChanges>(origin.Tree, target.Tree, new CompareOptions { Similarity = SimilarityOptions.None });
            return changes
                .SelectMany(change => new[] { change.OldPath, change.Path })
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(GlobPattern.NormalizePath)
                .Where(path => IsUnderRoot(path, root))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
        });

    public Task<IReadOnlyList<SourceCommit>?> ListFirstParentCommitsAsync(GovernanceSourceConfig config, string? afterSha, string headSha, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<SourceCommit>?>(config, headSha, cancellationToken, (_, head) =>
        {
            var chain = new List<SourceCommit>();
            var found = afterSha is null;
            foreach (var commit in FirstParentChain(head))
            {
                if (afterSha is not null && string.Equals(commit.Sha, afterSha, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

                chain.Add(ToSourceCommit(commit));
            }

            if (!found)
            {
                return null;
            }

            chain.Reverse();
            return chain;
        });

    public Task<IReadOnlyList<SourceFile>> ReadFilesAsync(GovernanceSourceConfig config, string commitSha, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ReadAsync<IReadOnlyList<SourceFile>>(config, commitSha, cancellationToken, (_, commit) =>
        {
            var root = Root(config);
            var files = new List<SourceFile>();
            foreach (var path in paths.Select(GlobPattern.NormalizePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (IsUnderRoot(path, root) && commit[path] is { TargetType: TreeEntryTargetType.Blob } entry && ToSourceFile(entry) is { } file)
                {
                    files.Add(file);
                }
            }

            return files;
        });
    }

    private static IEnumerable<Commit> FirstParentChain(Commit head)
    {
        for (var commit = head; commit is not null; commit = commit.Parents.FirstOrDefault())
        {
            yield return commit;
        }
    }

    private static SourceCommit ToSourceCommit(Commit commit) =>
        new(commit.Sha, commit.Committer.When.ToUniversalTime(), $"{commit.Author.Name} <{commit.Author.Email}>");

    /// <summary>Binary blobs are not governance text and are skipped.</summary>
    private static SourceFile? ToSourceFile(TreeEntry entry)
    {
        var blob = (Blob)entry.Target;
        return blob.IsBinary ? null : new SourceFile(GlobPattern.NormalizePath(entry.Path), blob.Sha, blob.GetContentText(Encoding.UTF8));
    }

    private static string Root(GovernanceSourceConfig config) => GlobPattern.NormalizePath(config.RootPath ?? string.Empty);

    private static bool IsUnderRoot(string path, string root) =>
        root.Length == 0 || (path.Length > root.Length && path[root.Length] == '/' && path.StartsWith(root, StringComparison.Ordinal));

    private static string BranchRef(GovernanceSourceConfig config) => "refs/heads/" + config.Branch;

    private async Task<GovernanceSourceCredential?> ResolveCredentialAsync(GovernanceSourceConfig config, CancellationToken cancellationToken)
    {
        EnsureAllowed(config);
        return await credentials.ResolveAsync(config, cancellationToken);
    }

    private void EnsureAllowed(GovernanceSourceConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.OrganizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.RepositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Branch);
        var url = config.RepositoryUrl;
        if (GovernanceSourceUrl.HasEmbeddedCredentials(url))
        {
            throw new InvalidOperationException("The governance repository URL must not embed credentials.");
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isLocal = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || (!url.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(url));
        if (!isLocal)
        {
            throw new InvalidOperationException("Governance repositories must be addressed by an https:// URL, a file:// URL or an absolute local path.");
        }

        if (!options.CurrentValue.AllowLocalRepositories)
        {
            throw new InvalidOperationException($"Local governance repositories are disabled. Set {GovernanceOptions.SectionName}:{nameof(GovernanceOptions.AllowLocalRepositories)} to allow them.");
        }
    }

    private string MirrorPath(GovernanceSourceConfig config)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{config.OrganizationId}\n{config.RepositoryUrl}"));
        return Path.Combine(Path.GetFullPath(options.CurrentValue.CacheDirectory), Convert.ToHexStringLower(digest.AsSpan(0, 16)) + ".git");
    }

    private static void Fetch(string mirror, GovernanceSourceConfig config, GovernanceSourceCredential? credential)
    {
        if (!Repository.IsValid(mirror))
        {
            Directory.CreateDirectory(mirror);
            Repository.Init(mirror, isBare: true);
        }

        using var repository = new Repository(mirror);
        if (repository.Network.Remotes[RemoteName] is null)
        {
            repository.Network.Remotes.Add(RemoteName, config.RepositoryUrl);
        }

        var fetch = new FetchOptions { Prune = true, TagFetchMode = TagFetchMode.None };
        if (credential is not null)
        {
            // The secret lives only in this callback; it is not written to the mirror's configuration.
            fetch.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = credential.Username, Password = credential.Secret };
        }

        var branch = BranchRef(config);
        try
        {
            Commands.Fetch(repository, RemoteName, [$"+{branch}:{branch}"], fetch, null);
        }
        catch (LibGit2SharpException ex)
        {
            // libgit2 messages can echo the remote URL but never the credential supplied by the callback.
            throw new InvalidOperationException($"The governance repository of organization '{config.OrganizationId}' could not be fetched: {ex.Message}", ex);
        }
    }

    /// <summary>Runs a read against the mirror, fetching once when the mirror does not have the commit yet.</summary>
    private async Task<T> ReadAsync<T>(GovernanceSourceConfig config, string commitSha, CancellationToken cancellationToken, Func<Repository, Commit, T> read)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        var credential = await ResolveCredentialAsync(config, cancellationToken);
        return await RunAsync(config, cancellationToken, mirror =>
        {
            if (!Repository.IsValid(mirror) || !HasCommit(mirror, commitSha))
            {
                Fetch(mirror, config, credential);
            }

            using var repository = new Repository(mirror);
            var commit = repository.Lookup<Commit>(commitSha)
                ?? throw new InvalidOperationException($"Commit '{commitSha}' does not exist in the governance repository of organization '{config.OrganizationId}'.");
            return read(repository, commit);
        });
    }

    private static bool HasCommit(string mirror, string commitSha)
    {
        using var repository = new Repository(mirror);
        return repository.Lookup<Commit>(commitSha) is not null;
    }

    private async Task<T> RunAsync<T>(GovernanceSourceConfig config, CancellationToken cancellationToken, Func<string, T> work)
    {
        var mirror = MirrorPath(config);
        var gate = Gates.GetOrAdd(mirror, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // libgit2 is synchronous; keep it off the caller's thread.
            return await Task.Run(() => work(mirror), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
