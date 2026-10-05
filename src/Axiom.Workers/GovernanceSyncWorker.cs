using Axiom.Application.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.Workers;

/// <summary>One governance source to seed into the registry, bound from <c>Axiom:Workers:GovernanceSync:Sources</c>.</summary>
public sealed class GovernanceSourceSeed
{
    public string OrganizationId { get; set; } = string.Empty;

    public string RepositoryUrl { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    /// <summary>Directory inside the repository that holds the records; empty means the repository root.</summary>
    public string RootPath { get; set; } = string.Empty;
}

public sealed class GovernanceSyncOptions
{
    public const string SectionName = "Axiom:Workers:GovernanceSync";

    public bool Enabled { get; set; } = true;

    /// <summary>How often every registered source is checked for a new head commit.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Sources written to the registry at startup (upsert: configuration wins over the stored location).
    /// URLs must not embed credentials; supply them with <c>Axiom:Governance:Credentials:&lt;host&gt;:Token</c>.
    /// </summary>
    public List<GovernanceSourceSeed> Sources { get; } = [];
}

/// <summary>
/// Seeds the configured governance sources into the registry, then keeps each registered source's
/// projection in step with its Git head. A failing source is logged and retried on the next cycle; it
/// never stops the others, and a rejected source leaves the previous snapshot in force (ADR-0008).
/// </summary>
public sealed partial class GovernanceSyncWorker(
    IServiceScopeFactory scopes,
    IOptions<GovernanceSyncOptions> options,
    TimeProvider time,
    ILogger<GovernanceSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled();
            return;
        }

        var interval = settings.Interval > TimeSpan.Zero ? settings.Interval : TimeSpan.FromMinutes(1);
        var seeded = false;
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                seeded = await RunCycleAsync(settings, seeded, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogCycleFailed(ex);
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

    private async Task<bool> RunCycleAsync(GovernanceSyncOptions settings, bool alreadySeeded, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<ISchemaGate>().IsReadyAsync(cancellationToken))
        {
            LogWaitingForSchema();
            return alreadySeeded;
        }

        var registry = scope.ServiceProvider.GetRequiredService<IGovernanceSourceRegistry>();

        if (!alreadySeeded)
        {
            foreach (var seed in settings.Sources)
            {
                await registry.UpsertAsync(new GovernanceSourceConfig(seed.OrganizationId, seed.RepositoryUrl, seed.Branch, seed.RootPath), cancellationToken);
                LogSeeded(seed.OrganizationId, seed.RepositoryUrl, seed.Branch);
            }
        }

        var synchronizer = scope.ServiceProvider.GetRequiredService<IGovernanceSynchronizer>();
        foreach (var source in await registry.ListAsync(cancellationToken))
        {
            try
            {
                var result = await synchronizer.SynchronizeAsync(source, SyncMode.Incremental, cancellationToken);
                switch (result.Outcome)
                {
                    case SyncOutcome.Published:
                        LogPublished(source.OrganizationId, result.SourceCommit, result.RecordCount, result.ChangedRecordIds.Length);
                        break;
                    case SyncOutcome.Rejected:
                        LogRejected(source.OrganizationId, result.SourceCommit, result.Issues.Length);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSourceFailed(ex, source.OrganizationId, source.RepositoryUrl);
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is not ready (migrations pending or database unreachable); governance sync waits.")]
    private partial void LogWaitingForSchema();

    [LoggerMessage(Level = LogLevel.Information, Message = "Governance sync is disabled.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Governance source for organization {Organization} registered: {Url} @ {Branch}.")]
    private partial void LogSeeded(string organization, string url, string branch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Governance published for {Organization} at {Commit}: {Records} records, {Changed} changed.")]
    private partial void LogPublished(string organization, string commit, int records, int changed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Governance source for {Organization} at {Commit} was rejected with {Issues} issue(s); the previous snapshot stays in force.")]
    private partial void LogRejected(string organization, string commit, int issues);

    [LoggerMessage(Level = LogLevel.Error, Message = "Governance sync failed for {Organization} ({Url}); will retry.")]
    private partial void LogSourceFailed(Exception exception, string organization, string url);

    [LoggerMessage(Level = LogLevel.Error, Message = "Governance sync cycle failed; will retry.")]
    private partial void LogCycleFailed(Exception exception);
}
