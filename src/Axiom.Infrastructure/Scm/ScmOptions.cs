namespace Axiom.Infrastructure.Scm;

/// <summary>Settings of the source-control adapter, bound from <c>Axiom:Scm</c>.</summary>
public sealed class ScmOptions
{
    public const string SectionName = "Axiom:Scm";

    /// <summary>Directory holding one bare mirror per repository. It is a cache: deleting it only costs a re-fetch.</summary>
    public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "axiom", "scm-mirrors");

    /// <summary>
    /// Hosts Axiom may fetch from. Clone URLs come from catalog data that repositories themselves
    /// declare, so a host is contacted only when it is listed here or has credentials configured.
    /// </summary>
    public List<string> AllowedHosts { get; } = [];

    /// <summary>Allows repositories addressed by a local path or <c>file://</c> URL. Off by default.</summary>
    public bool AllowLocalRepositories { get; set; }

    /// <summary>Tokens for HTTPS hosts, by host name. They are sent only to the host they are configured for.</summary>
    public Dictionary<string, ScmHostCredential> Credentials { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most changed files evaluated from one diff; the rest are reported as truncated.</summary>
    public int MaxChangedFiles { get; set; } = 2_000;
}

public sealed class ScmHostCredential
{
    public string Username { get; set; } = "x-access-token";

    public string? Token { get; set; }
}
