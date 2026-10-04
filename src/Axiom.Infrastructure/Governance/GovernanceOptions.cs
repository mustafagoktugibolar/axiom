namespace Axiom.Infrastructure.Governance;

/// <summary>Settings of the governance registry, bound from <c>Axiom:Governance</c>.</summary>
public sealed class GovernanceOptions
{
    public const string SectionName = "Axiom:Governance";

    /// <summary>Directory holding one bare Git mirror per governance source. It is a cache: deleting it only costs a re-fetch.</summary>
    public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "axiom", "governance-mirrors");

    /// <summary>
    /// Allows governance sources addressed by a local path or <c>file://</c> URL. Off by default so a
    /// registered source cannot make the service read arbitrary repositories from its own disk.
    /// </summary>
    public bool AllowLocalRepositories { get; set; }

    /// <summary>
    /// Access tokens for HTTPS sources by host name, for example
    /// <c>Axiom:Governance:Credentials:github.com:Token</c>. Supply them from a secret store or the
    /// environment; they are sent only to the host they are configured for.
    /// </summary>
    public Dictionary<string, GovernanceHostCredential> Credentials { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GovernanceHostCredential
{
    /// <summary>User name sent with the token. Most Git hosts accept any non-empty value for token authentication.</summary>
    public string Username { get; set; } = "x-access-token";

    public string? Token { get; set; }
}
