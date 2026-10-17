using Axiom.Application.Common;
using Microsoft.Extensions.Logging;

namespace Axiom.Infrastructure.Events;

/// <summary>Writes one structured line per event. The payload is deliberately not logged: it may carry sensitive content.</summary>
public sealed partial class LogEventSink(ILogger<LogEventSink> logger) : IEventSink
{
    public string Name => "log";

    public Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogEvent(message.EventId, message.EventType, message.OrganizationId, message.OccurredAt, message.Attempts);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Integration event {EventId} ({EventType}) for {Organization} occurred at {OccurredAt}; prior attempts {Attempts}.")]
    private partial void LogEvent(string eventId, string eventType, string organization, DateTimeOffset occurredAt, int attempts);
}
