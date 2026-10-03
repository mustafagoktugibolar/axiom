using Axiom.Domain.Catalog;

namespace Axiom.Infrastructure.Catalog;

/// <summary>The alias forms under which a repository entity is bound, in descending resolution priority.</summary>
internal static class RepositoryAliases
{
    public const string Name = "name";
    public const string Url = "url";
    public const string Declared = "alias";
    public const string UrlPath = "path";
    public const string SimpleName = "simple";

    /// <summary>Lower value wins. An exact name beats a clone URL, which beats declared aliases, URL paths and bare simple names.</summary>
    public static int Priority(string aliasType) => aliasType switch
    {
        Name => 0,
        Url => 1,
        Declared => 2,
        UrlPath => 3,
        SimpleName => 4,
        _ => int.MaxValue,
    };

    /// <summary>All (alias, type) pairs a repository entity can be resolved by.</summary>
    public static HashSet<(string Alias, string Type)> For(EntityRef repository, IReadOnlyDictionary<string, string> attributes)
    {
        var aliases = new HashSet<(string, string)> { (repository.Name, Name) };
        if (repository.SimpleName != repository.Name)
        {
            aliases.Add((repository.SimpleName, SimpleName));
        }

        if (attributes.TryGetValue(RepositoryAttributes.Aliases, out var declared))
        {
            foreach (var alias in RepositoryAttributes.SplitList(declared))
            {
                aliases.Add((alias.ToLowerInvariant(), Declared));
            }
        }

        if (attributes.TryGetValue(RepositoryAttributes.CloneUrls, out var urls))
        {
            foreach (var url in RepositoryAttributes.SplitList(urls))
            {
                if (RepositoryUrl.Normalize(url) is not { } normalized)
                {
                    continue;
                }

                aliases.Add((normalized, Url));
                if (RepositoryUrl.PathOf(normalized) is { } path)
                {
                    aliases.Add((path, UrlPath));
                }
            }
        }

        return aliases;
    }
}
