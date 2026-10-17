using System.Net;
using Axiom.Application.Common;
using Axiom.Infrastructure.Events;
using Axiom.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Axiom.UnitTests.Workers;

public sealed class OutboxDispatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeStore _store = new();
    private readonly EventsOptions _options = new() { Enabled = true, MaxAttempts = 3, InitialBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(30) };

    private static OutboxMessage Message(int attempts = 0) =>
        new("evt_1", "acme", EventTypes.EvaluationCompleted, "1", Start, null, "{\"verdict\":\"allow\"}", attempts);

    private OutboxDispatcher Dispatcher(params IEventSink[] sinks) =>
        new(sinks, Options.Create(_options), _time, NullLogger<OutboxDispatcher>.Instance);

    [Fact]
    public async Task Event_accepted_by_all_sinks_is_marked_delivered()
    {
        _store.Pending.Add(Message());
        var a = new FakeSink("a");
        var b = new FakeSink("b");

        var claimed = await Dispatcher(a, b).DispatchBatchAsync(_store, CancellationToken.None);

        Assert.Equal(1, claimed);
        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Single(_store.Delivered);
        Assert.Empty(_store.Failed);
    }

    [Fact]
    public async Task Failing_sink_does_not_stop_the_other_sinks_and_the_event_is_retried()
    {
        _store.Pending.Add(Message());
        var bad = new FakeSink("bad") { Fail = true };
        var good = new FakeSink("good");

        await Dispatcher(bad, good).DispatchBatchAsync(_store, CancellationToken.None);

        Assert.Equal(1, good.Calls);
        Assert.Empty(_store.Delivered);
        var failure = Assert.Single(_store.Failed);
        Assert.NotNull(failure.RetryAt);
        Assert.Contains("bad", failure.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_backs_off_exponentially_up_to_the_cap()
    {
        var sink = new FakeSink("bad") { Fail = true };
        var dispatcher = Dispatcher(sink);

        _store.Pending.Add(Message(attempts: 0));
        await dispatcher.DispatchBatchAsync(_store, CancellationToken.None);
        _store.Pending.Add(Message(attempts: 1));
        await dispatcher.DispatchBatchAsync(_store, CancellationToken.None);

        Assert.Equal(Start.AddSeconds(10), _store.Failed[0].RetryAt);
        Assert.Equal(Start.AddSeconds(20), _store.Failed[1].RetryAt);
        Assert.Equal(TimeSpan.FromSeconds(30), OutboxDispatcher.Backoff(_options, 3));
        Assert.Equal(TimeSpan.FromSeconds(30), OutboxDispatcher.Backoff(_options, 500));
    }

    [Fact]
    public async Task Event_is_dead_lettered_after_the_maximum_number_of_attempts()
    {
        _store.Pending.Add(Message(attempts: 2));

        await Dispatcher(new FakeSink("bad") { Fail = true }).DispatchBatchAsync(_store, CancellationToken.None);

        var failure = Assert.Single(_store.Failed);
        Assert.Null(failure.RetryAt);
        Assert.Contains("bad", failure.Error, StringComparison.Ordinal);
        Assert.Empty(_store.Delivered);
    }

    [Fact]
    public async Task Worker_does_nothing_when_disabled()
    {
        var options = new EventsOptions();
        var services = new ServiceCollection().AddSingleton(_store).AddSingleton<IOutboxStore>(_store).BuildServiceProvider();
        var worker = new OutboxDispatcherWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), _time, NullLogger<OutboxDispatcherWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _store.ClaimCalls);
    }

    [Fact]
    public async Task Worker_delivers_when_enabled_and_the_schema_is_ready()
    {
        _store.Pending.Add(Message());
        var gate = Substitute.For<ISchemaGate>();
        gate.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var sink = new FakeSink("a");
        var services = new ServiceCollection()
            .AddSingleton<IOutboxStore>(_store).AddSingleton(gate).AddSingleton<IEventSink>(sink)
            .AddSingleton(Options.Create(_options)).AddSingleton<TimeProvider>(_time)
            .AddLogging().AddScoped<OutboxDispatcher>().BuildServiceProvider();
        var worker = new OutboxDispatcherWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options), _time, NullLogger<OutboxDispatcherWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, sink.Calls);
        Assert.Single(_store.Delivered);
    }

    [Fact]
    public async Task Worker_leaves_events_alone_while_the_schema_is_not_ready()
    {
        var gate = Substitute.For<ISchemaGate>();
        gate.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(false);
        var services = new ServiceCollection()
            .AddSingleton<IOutboxStore>(_store).AddSingleton(gate).AddSingleton<IEventSink>(new FakeSink("a"))
            .AddSingleton(Options.Create(_options)).AddSingleton<TimeProvider>(_time)
            .AddLogging().AddScoped<OutboxDispatcher>().BuildServiceProvider();
        var worker = new OutboxDispatcherWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options), _time, NullLogger<OutboxDispatcherWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _store.ClaimCalls);
    }

    [Fact]
    public async Task Webhook_sink_signs_the_body_and_sends_the_event_id()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sink = new WebhookEventSink(new WebhookEndpointOptions { Name = "crm", Url = "https://hooks.example.test/axiom", Secret = "s3cret" }, new FakeHttpFactory(handler), _time);

        await sink.DeliverAsync(Message(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("evt_1", request.Headers["X-Axiom-Event-Id"]);
        var timestamp = request.Headers["X-Axiom-Timestamp"];
        Assert.Equal(WebhookEventSink.Sign("s3cret"u8.ToArray(), timestamp, request.Body), request.Headers["X-Axiom-Signature"]);
        Assert.Contains("\"eventId\":\"evt_1\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"allow\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_sink_treats_non_success_and_redirect_responses_as_failures()
    {
        var sink = new WebhookEventSink(new WebhookEndpointOptions { Name = "crm", Url = "https://hooks.example.test/axiom", Secret = "s3cret" },
            new FakeHttpFactory(new CapturingHandler(HttpStatusCode.Found)), _time);

        await Assert.ThrowsAsync<HttpRequestException>(() => sink.DeliverAsync(Message(), CancellationToken.None));
    }

    [Theory]
    [InlineData("http://hooks.example.test/axiom", "s3cret")]
    [InlineData("not a url", "s3cret")]
    [InlineData("https://hooks.example.test/axiom", "")]
    public void Webhook_sink_rejects_non_https_urls_and_missing_secrets(string url, string secret) =>
        Assert.Throws<InvalidOperationException>(() => new WebhookEventSink(new WebhookEndpointOptions { Name = "x", Url = url, Secret = secret }, new FakeHttpFactory(new CapturingHandler(HttpStatusCode.OK)), _time));

    [Fact]
    public void Delivery_is_disabled_by_default() => Assert.False(new EventsOptions().Enabled);

    private sealed class FakeSink(string name) : IEventSink
    {
        public string Name => name;

        public bool Fail { get; init; }

        public int Calls { get; private set; }

        public Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Calls++;
            return Fail ? Task.FromException(new HttpRequestException("boom")) : Task.CompletedTask;
        }
    }

    private sealed class FakeStore : IOutboxStore
    {
        public List<OutboxMessage> Pending { get; } = [];

        public List<OutboxMessage> Delivered { get; } = [];

        public List<(OutboxMessage Message, DateTimeOffset? RetryAt, string Error)> Failed { get; } = [];

        public int ClaimCalls { get; private set; }

        public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int limit, TimeSpan lease, CancellationToken cancellationToken)
        {
            ClaimCalls++;
            var batch = Pending.Take(limit).ToList();
            Pending.RemoveRange(0, batch.Count);
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(batch);
        }

        public Task MarkDeliveredAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Delivered.Add(message);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(OutboxMessage message, DateTimeOffset? retryAt, string reason, CancellationToken cancellationToken)
        {
            Failed.Add((message, retryAt, reason));
            return Task.CompletedTask;
        }
    }

    private sealed record Captured(Dictionary<string, string> Headers, string Body);

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Requests.Add(new Captured(headers, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
