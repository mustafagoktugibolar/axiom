using System.Text;
using Axiom.Domain.Policy;
using LibGit2Sharp;

namespace Axiom.Infrastructure.Scm;

/// <summary>
/// Read-only view of a repository at one commit, backed by a local mirror. The path list is
/// materialized once; content is read on demand and capped, so a huge blob cannot exhaust memory.
/// </summary>
internal sealed class GitRepositoryView(string mirrorPath, string commitSha, IReadOnlyList<string> paths) : IRepositoryView
{
    private readonly HashSet<string> _set = new(paths, StringComparer.Ordinal);

    public IEnumerable<string> Paths => paths;

    public bool Exists(string path) => _set.Contains(path);

    public string? Read(string path)
    {
        if (!_set.Contains(path))
        {
            return null;
        }

        using var repository = new Repository(mirrorPath);
        return repository.Lookup<Commit>(commitSha)?[path]?.Target is Blob blob ? ReadBlob(blob) : null;
    }

    /// <summary>Text of a blob, or null for binary content. Longer text is cut one character past the policy limit.</summary>
    internal static string? ReadBlob(Blob blob)
    {
        if (blob.IsBinary)
        {
            return null;
        }

        using var reader = new StreamReader(blob.GetContentStream(), Encoding.UTF8);
        var buffer = new char[PolicyLimits.MaxFileCharacters + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }
}
