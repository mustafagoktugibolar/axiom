using Axiom.Application.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.Workers;

public sealed class GovernanceHealthWorkerOptions
{
    public const string SectionName = "Axiom:Workers:GovernanceHealth";

    public bool Enabled { get; set; } = true;

    /// <summary>How often each organization's governance is checked for stale records and expiring exceptions.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Periodically computes governance health per registered organization: it flags records past their
/// review date (<c>governance.record.stale</c>), announces exceptions about to expire
/// (<c>exception.expiring</c>) and logs the health totals. Both notices are idempotent, so an
/// interval shorter than the review or expiry window never re-notifies. A failing organization is
/// logged and retried on the next cycle.
/// </summary>
public sealed partial class GovernanceHealthWorker(
    IServiceScopeFactory scopes,
    IOptions<GovernanceHealthWorkerOptions> options,
    IOptions<GovernanceHealthOptions> health,
    TimeProvider time,
    ILogger<GovernanceHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled();
            return;
        }

        var interval = settings.Interval > TimeSpan.Zero ? settings.Interval : TimeSpan.FromHours(1);
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
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

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<ISchemaGate>().IsReadyAsync(cancellationToken))
        {
            LogWaitingForSchema();
            return;
        }

        var registry = scope.ServiceProvider.GetRequiredService<IGovernanceSourceRegistry>();
        foreach (var source in await registry.ListAsync(cancellationToken))
        {
            try
            {
                await CheckAsync(scope.ServiceProvider, source.OrganizationId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogOrganizationFailed(ex, source.OrganizationId);
            }
        }
    }

    private async Task CheckAsync(IServiceProvider services, string organizationId, CancellationToken cancellationToken)
    {
        var snapshot = await services.GetRequiredService<IGovernanceSnapshotProvider>().GetCurrentAsync(organizationId, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        var report = GovernanceHealthCalculator.Compute(snapshot, time.GetUtcNow(), health.Value.ExpiryWindow);
        var stale = await services.GetRequiredService<StaleRecordDetector>().DetectAsync(organizationId, cancellationToken);
        var expiring = await services.GetRequiredService<ExceptionExpiryNotifier>().NotifyAsync(organizationId, cancellationToken);
        LogHealth(
            organizationId, report.StaleRecords.Length, report.RecordsWithoutOwners.Length, report.DeprecatedStillReferenced.Length,
            report.ExpiringExceptions.Length, stale.NewlyFlagged, expiring);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is not ready (migrations pending or database unreachable); governance health waits.")]
    private partial void LogWaitingForSchema();

    [LoggerMessage(Level = LogLevel.Information, Message = "Governance health check is disabled.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Governance health for {Organization}: {Stale} stale, {Ownerless} without owners, {Deprecated} deprecated but referenced, {Expiring} exceptions expiring; {NewStale} stale and {NewExpiring} expiry notice(s) queued.")]
    private partial void LogHealth(string organization, int stale, int ownerless, int deprecated, int expiring, int newStale, int newExpiring);

    [LoggerMessage(Level = LogLevel.Error, Message = "Governance health check failed for {Organization}; will retry.")]
    private partial void LogOrganizationFailed(Exception exception, string organization);

    [LoggerMessage(Level = LogLevel.Error, Message = "Governance health cycle failed; will retry.")]
    private partial void LogCycleFailed(Exception exception);
}
