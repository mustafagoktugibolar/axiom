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

    /// <summary>
    /// Providers that receive status checks, by the host of the repository's clone URL. A provider is
    /// contacted only for repositories on its host, and its token is sent only to its API base URL.
    /// </summary>
    public Dictionary<string, ScmProviderOptions> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Publish pull-request verdicts as status checks. Off by default.</summary>
    public bool PublishStatus { get; set; }

    /// <summary>Base URL of the Axiom portal, used to link a check to its evaluation.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Most changed files evaluated from one diff; the rest are reported as truncated.</summary>
    public int MaxChangedFiles { get; set; } = 2_000;
}

public sealed class ScmHostCredential
{
    public string Username { get; set; } = "x-access-token";

    public string? Token { get; set; }
}

public enum ScmProviderType
{
    GitHub,
    AzureDevOps,
}

public sealed class ScmProviderOptions
{
    public ScmProviderType Type { get; set; }

    /// <summary>
    /// GitHub: <c>https://api.github.com</c>, or <c>https://host/api/v3</c> for GitHub Enterprise Server.
    /// Azure DevOps: leave empty; the API base is derived from the clone URL.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>GitHub: an installation or fine-grained token with <c>checks:write</c>. Azure DevOps: a PAT with <c>Code (status)</c>.</summary>
    public string? Token { get; set; }

    /// <summary>Name under which the check appears.</summary>
    public string CheckName { get; set; } = "Axiom governance";
}
