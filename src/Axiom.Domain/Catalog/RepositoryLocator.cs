using System.Collections.Immutable;

namespace Axiom.Domain.Catalog;

/// <summary>
/// Well-known attribute keys through which a <see cref="EntityKind.Repository"/> entity declares how it
/// can be located. Values are lists separated by commas or whitespace.
/// </summary>
public static class RepositoryAttributes
{
    /// <summary>Clone URLs of the repository in any common form (https, ssh, scp-like).</summary>
    public const string CloneUrls = "cloneUrls";

    /// <summary>Additional names the repository is known by, e.g. <c>acme/gateway</c>.</summary>
    public const string Aliases = "aliases";

    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];

    public static ImmutableArray<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static string JoinList(IEnumerable<string> values) => string.Join(' ', values);
}

/// <summary>
/// Normalizes clone URLs so that the https, ssh and scp-like spellings of the same repository compare
/// equal: scheme, credentials, default port, query, <c>.git</c> suffix and trailing slashes are removed
/// and the host is lower-cased. The path keeps its case.
/// </summary>
public static class RepositoryUrl
{
    /// <summary>
    /// True for an absolute filesystem path or a <c>file://</c> URL. Such a location has no host, so it cannot be
    /// normalized or matched as an alias; it is only usable where local repositories are explicitly allowed
    /// (<c>Axiom:Scm:AllowLocalRepositories</c>), which is off by default.
    /// </summary>
    public static bool IsLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || (!value.Contains("://", StringComparison.Ordinal) && (value.StartsWith('/') || (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')));
    }

    /// <summary>Returns <c>host/path</c>, or null when the value has no host.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        string? scheme = null;
        string authority;
        string path;

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            scheme = text[..schemeEnd].ToLowerInvariant();
            text = text[(schemeEnd + 3)..];
            (authority, path) = SplitAtFirstSlash(text);
        }
        else if (IsScpLike(text, out var colon))
        {
            // user@host:path — the colon separates host and path, it is not a port.
            authority = text[..colon];
            path = text[(colon + 1)..];
        }
        else
        {
            (authority, path) = SplitAtFirstSlash(text);
        }

        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        var host = StripDefaultPort(authority.ToLowerInvariant(), scheme);
        if (host.Length == 0)
        {
            return null;
        }

        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4].TrimEnd('/');
        }

        return path.Length == 0 ? host : $"{host}/{path}";
    }

    /// <summary>The lower-cased path part of a normalized URL (<c>acme/gateway</c>), or null when there is none.</summary>
    public static string? PathOf(string normalizedUrl)
    {
        ArgumentNullException.ThrowIfNull(normalizedUrl);
        var slash = normalizedUrl.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 || slash == normalizedUrl.Length - 1 ? null : normalizedUrl[(slash + 1)..].ToLowerInvariant();
    }

    private static (string Authority, string Path) SplitAtFirstSlash(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (text, string.Empty) : (text[..slash], text[(slash + 1)..]);
    }

    private static bool IsScpLike(string text, out int colon)
    {
        colon = text.IndexOf(':', StringComparison.Ordinal);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var at = text.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && colon > at && (slash < 0 || colon < slash) && !text.Contains('[', StringComparison.Ordinal);
    }

    private static string StripDefaultPort(string authority, string? scheme)
    {
        var colon = authority.LastIndexOf(':');
        if (colon < 0 || authority.EndsWith(']'))
        {
            return authority;
        }

        var port = authority[(colon + 1)..];
        var isDefault = (scheme, port) is ("https", "443") or ("http", "80") or ("ssh", "22") or ("git", "9418");
        return isDefault || port.Length == 0 ? authority[..colon] : authority;
    }
}
