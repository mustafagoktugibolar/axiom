using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Policy;

/// <summary>
/// Immutable <see cref="IRepositoryView"/> over file contents the caller already holds. Paths are
/// normalized the same way globs are, so lookups are independent of separator style.
/// </summary>
public sealed class InMemoryRepositoryView : IRepositoryView
{
    private readonly ImmutableSortedDictionary<string, string> _files;

    public InMemoryRepositoryView(IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            var normalized = GlobPattern.NormalizePath(path);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("A repository path must not be empty.", nameof(files));
            }

            builder[normalized] = content ?? string.Empty;
        }

        _files = builder.ToImmutable();
    }

    public static InMemoryRepositoryView Empty { get; } = new([]);

    public IEnumerable<string> Paths => _files.Keys;

    /// <summary>Builds a view from path/content pairs.</summary>
    public static InMemoryRepositoryView Of(params (string Path, string Content)[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new InMemoryRepositoryView(files.Select(f => new KeyValuePair<string, string>(f.Path, f.Content)));
    }

    /// <summary>The tree as it looks after the change: every non-deleted changed file is present.</summary>
    public static InMemoryRepositoryView FromChanges(IEnumerable<ChangedFile> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return new InMemoryRepositoryView(changes
            .Where(c => c.Kind != ChangeKind.Deleted)
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => new KeyValuePair<string, string>(c.Path, c.Content ?? string.Empty)));
    }

    public bool Exists(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _files.ContainsKey(GlobPattern.NormalizePath(path));
    }

    public string? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _files.GetValueOrDefault(GlobPattern.NormalizePath(path));
    }
}
