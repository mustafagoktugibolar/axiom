using Axiom.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Infrastructure.Events;

public static class EventsModule
{
    /// <summary>
    /// Registers the outbox store and the sinks configured under <c>Axiom:Events</c>. An enabled webhook with a
    /// non-https URL or no secret fails startup rather than silently dropping its events.
    /// </summary>
    public static IServiceCollection AddEventDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EventsOptions>().Bind(configuration.GetSection(EventsOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IOutboxStore, EfOutboxStore>();

        // Redirects are not followed: a signed body must never be replayed to another host.
        services.AddHttpClient(WebhookEventSink.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        var options = new EventsOptions();
        configuration.GetSection(EventsOptions.SectionName).Bind(options);
        if (options.LogSink)
        {
            services.AddSingleton<IEventSink, LogEventSink>();
        }

        foreach (var endpoint in options.Webhooks.Where(w => w.Enabled))
        {
            services.AddSingleton<IEventSink>(sp => new WebhookEventSink(
                endpoint, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<TimeProvider>()));
        }

        return services;
    }
}
