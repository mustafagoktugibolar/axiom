using Axiom.Application.Common;
using Microsoft.Extensions.Options;

namespace Axiom.Workers;

/// <summary>
/// Delivers claimed outbox events to every sink. An event counts as delivered only when all sinks accepted it;
/// a failing sink never keeps the others from being tried. Failures are retried with exponential backoff and,
/// after <see cref="EventsOptions.MaxAttempts"/>, dead-lettered: the row stays, flagged and with its last error.
/// A retry re-sends to every sink, so consumers de-duplicate on the event ID.
/// </summary>
public sealed partial class OutboxDispatcher(
    IEnumerable<IEventSink> sinks,
    IOptions<EventsOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger)
{
    private readonly IReadOnlyList<IEventSink> _sinks = [.. sinks];

    public bool HasSinks => _sinks.Count > 0;

    /// <summary>Claims and delivers one batch; returns how many events were claimed.</summary>
    public async Task<int> DispatchBatchAsync(IOutboxStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var settings = options.Value;
        var batch = await store.ClaimAsync(Math.Max(1, settings.BatchSize), settings.LeaseDuration, cancellationToken);
        foreach (var message in batch)
        {
            await DeliverAsync(store, message, settings, cancellationToken);
        }

        return batch.Count;
    }

    /// <summary>Delay before the attempt after <paramref name="failedAttempts"/> failures: initial, doubled each time, capped.</summary>
    public static TimeSpan Backoff(EventsOptions settings, int failedAttempts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var factor = Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 30));
        var seconds = Math.Min(settings.MaxBackoff.TotalSeconds, settings.InitialBackoff.TotalSeconds * factor);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task DeliverAsync(IOutboxStore store, OutboxMessage message, EventsOptions settings, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.DeliverAsync(message, cancellationToken);
                AxiomTelemetry.EventsDelivered.Add(1, new KeyValuePair<string, object?>("sink", sink.Name));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                AxiomTelemetry.EventsFailed.Add(1, new KeyValuePair<string, object?>("sink", sink.Name));
                errors.Add($"{sink.Name}: {ex.GetType().Name}: {ex.Message}");
                LogSinkFailed(ex, message.EventId, sink.Name, message.Attempts + 1);
            }
        }

        if (errors.Count == 0)
        {
            await store.MarkDeliveredAsync(message, cancellationToken);
            AxiomTelemetry.EventsLag.Record((time.GetUtcNow() - message.OccurredAt).TotalSeconds);
            return;
        }

        var attempt = message.Attempts + 1;
        var error = string.Join("; ", errors);
        if (attempt >= settings.MaxAttempts)
        {
            await store.MarkFailedAsync(message, null, error, cancellationToken);
            AxiomTelemetry.EventsDeadLettered.Add(1, new KeyValuePair<string, object?>("event_type", message.EventType));
            LogDeadLettered(message.EventId, message.EventType, message.OrganizationId, attempt);
            return;
        }

        await store.MarkFailedAsync(message, time.GetUtcNow() + Backoff(settings, attempt), error, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery of event {EventId} to {Sink} failed (attempt {Attempt}); it will be retried.")]
    private partial void LogSinkFailed(Exception exception, string eventId, string sink, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event {EventId} ({EventType}) for {Organization} was dead-lettered after {Attempts} attempts; it stays in the outbox for inspection.")]
    private partial void LogDeadLettered(string eventId, string eventType, string organization, int attempts);
}

/// <summary>
/// Drains the transactional outbox on an interval. Disabled unless <c>Axiom:Events:Enabled</c> is set, and
/// idle until at least one sink is configured, so events are never marked delivered into the void. Replicas
/// share the work through row leases (see <see cref="IOutboxStore"/>). A cycle failure is logged and retried.
/// </summary>
public sealed partial class OutboxDispatcherWorker(
    IServiceScopeFactory scopes,
    IOptions<EventsOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled();
            return;
        }

        var interval = settings.PollInterval > TimeSpan.Zero ? settings.PollInterval : TimeSpan.FromSeconds(5);
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await RunCycleAsync(settings, stoppingToken);
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

    private async Task RunCycleAsync(EventsOptions settings, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<ISchemaGate>().IsReadyAsync(cancellationToken))
        {
            LogWaitingForSchema();
            return;
        }

        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        if (!dispatcher.HasSinks)
        {
            LogNoSinks();
            return;
        }

        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        // A full batch means more is probably waiting: keep draining before sleeping.
        while (await dispatcher.DispatchBatchAsync(store, cancellationToken) >= Math.Max(1, settings.BatchSize))
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is not ready (migrations pending or database unreachable); event dispatch waits.")]
    private partial void LogWaitingForSchema();

    [LoggerMessage(Level = LogLevel.Information, Message = "Event dispatch is disabled.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event dispatch is enabled but no sink is configured under Axiom:Events; events stay in the outbox.")]
    private partial void LogNoSinks();

    [LoggerMessage(Level = LogLevel.Error, Message = "Event dispatch cycle failed; will retry.")]
    private partial void LogCycleFailed(Exception exception);
}
