using Axiom.Application.Catalog;
using Axiom.Application.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.Workers;

public sealed class CatalogSyncOptions
{
    public const string SectionName = "Axiom:Workers:CatalogSync";

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Git repositories whose catalog manifests (<c>.axiom/catalog.yaml</c>, <c>catalog-info.yaml</c>) feed
    /// the System Graph. <see cref="GovernanceSourceSeed.RootPath"/> limits where manifests are looked for.
    /// A repository is read again only when its branch head moves.
    /// </summary>
    public List<GovernanceSourceSeed> Sources { get; } = [];
}

/// <summary>
/// Reads repository catalog manifests from the configured Git sources and imports them into the System
/// Graph. A manifest with any error is skipped whole (the parser rejects it), so a half-understood file
/// never retracts facts an earlier import established.
/// </summary>
public sealed partial class CatalogSyncWorker(
    IServiceScopeFactory scopes,
    IOptions<CatalogSyncOptions> options,
    TimeProvider time,
    ILogger<CatalogSyncWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, string> _lastHead = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || settings.Sources.Count == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Interval > TimeSpan.Zero ? settings.Interval : TimeSpan.FromMinutes(5), time);
        do
        {
            foreach (var seed in settings.Sources)
            {
                try
                {
                    await SyncAsync(seed, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(ex, seed.OrganizationId, seed.RepositoryUrl);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SyncAsync(GovernanceSourceSeed seed, CancellationToken cancellationToken)
    {
        var config = new GovernanceSourceConfig(seed.OrganizationId, seed.RepositoryUrl, seed.Branch, seed.RootPath);
        await using var scope = scopes.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<ISchemaGate>().IsReadyAsync(cancellationToken))
        {
            return;
        }

        var source = scope.ServiceProvider.GetRequiredService<IGovernanceSource>();
        var head = await source.FetchHeadAsync(config, cancellationToken);
        var key = $"{seed.OrganizationId}|{seed.RepositoryUrl}|{seed.Branch}|{seed.RootPath}";
        if (_lastHead.TryGetValue(key, out var seen) && seen == head.Sha)
        {
            return;
        }

        var parser = scope.ServiceProvider.GetRequiredService<ICatalogManifestParser>();
        var writer = scope.ServiceProvider.GetRequiredService<ICatalogWriter>();
        var manifests = (await source.ReadTreeAsync(config, head.Sha, cancellationToken))
            .Where(file => parser.ManifestPaths.Any(path => file.Path.Equals(path, StringComparison.Ordinal)
                || file.Path.EndsWith("/" + path, StringComparison.Ordinal)))
            .ToList();

        var clean = true;
        foreach (var file in manifests)
        {
            var parsed = parser.Parse(seed.OrganizationId, seed.RepositoryUrl, file.Path, file.Content, time.GetUtcNow());
            if (parsed.Import is null)
            {
                clean = false;
                LogRejected(seed.OrganizationId, file.Path, string.Join("; ", parsed.Errors));
                continue;
            }

            var result = await writer.ImportAsync(seed.OrganizationId, parsed.Import, cancellationToken);
            LogImported(seed.OrganizationId, file.Path, result.EntitiesUpserted, result.EdgesUpserted);
        }

        if (manifests.Count == 0)
        {
            LogNoManifest(seed.OrganizationId, seed.RepositoryUrl);
        }

        // Rejected manifests are retried next cycle; a clean pass is remembered until the head moves.
        if (clean)
        {
            _lastHead[key] = head.Sha;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Catalog for {Organization}: imported {Path} ({Entities} entities, {Edges} edges).")]
    private partial void LogImported(string organization, string path, int entities, int edges);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog manifest {Path} for {Organization} rejected: {Errors}")]
    private partial void LogRejected(string organization, string path, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "No catalog manifest found in {Url} for {Organization}.")]
    private partial void LogNoManifest(string organization, string url);

    [LoggerMessage(Level = LogLevel.Error, Message = "Catalog sync failed for {Organization} ({Url}); will retry.")]
    private partial void LogFailed(Exception exception, string organization, string url);
}
