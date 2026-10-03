using Axiom.Application.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Governance.Git;

/// <summary>
/// Resolves the access token for a governance source from configuration, by host. A token is only
/// ever offered to the HTTPS host it is configured for.
/// </summary>
public sealed class ConfiguredGovernanceSourceCredentials(IOptionsMonitor<GovernanceOptions> options) : IGovernanceSourceCredentials
{
    public ValueTask<GovernanceSourceCredential?> ResolveAsync(GovernanceSourceConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Uri.TryCreate(config.RepositoryUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && options.CurrentValue.Credentials.TryGetValue(uri.Host, out var credential)
            && !string.IsNullOrEmpty(credential.Token))
        {
            return ValueTask.FromResult<GovernanceSourceCredential?>(new GovernanceSourceCredential(credential.Username, credential.Token));
        }

        return ValueTask.FromResult<GovernanceSourceCredential?>(null);
    }
}
