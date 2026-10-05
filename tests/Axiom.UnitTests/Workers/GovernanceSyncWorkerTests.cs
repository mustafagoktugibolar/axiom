using System.Collections.Immutable;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Axiom.UnitTests.Workers;

public sealed class GovernanceSyncWorkerTests
{
    private static readonly GovernanceSourceSeed Seed = new() { OrganizationId = "acme", RepositoryUrl = "https://git.example.test/acme/governance.git", Branch = "main" };

    private readonly IGovernanceSourceRegistry _registry = Substitute.For<IGovernanceSourceRegistry>();
    private readonly IGovernanceSynchronizer _synchronizer = Substitute.For<IGovernanceSynchronizer>();
    private readonly ISchemaGate _gate = Substitute.For<ISchemaGate>();
    private readonly FakeTimeProvider _time = new();

    public GovernanceSyncWorkerTests() => _gate.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(true);

    private GovernanceSyncWorker Worker(GovernanceSyncOptions options)
    {
        var services = new ServiceCollection().AddSingleton(_registry).AddSingleton(_synchronizer).AddSingleton(_gate).BuildServiceProvider();
        return new GovernanceSyncWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), _time, NullLogger<GovernanceSyncWorker>.Instance);
    }

    private static SyncResult Result(SyncOutcome outcome) => new(outcome, "abc123", "snap", 3, [], ImmutableArray<ValidationIssue>.Empty);

    private static async Task RunOneCycleAsync(GovernanceSyncWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100); // let the first cycle run; the timer only ticks when the fake clock advances
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Configured_sources_are_seeded_into_the_registry_and_synchronized()
    {
        var config = new GovernanceSourceConfig("acme", Seed.RepositoryUrl, "main", string.Empty);
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns([config]);
        _synchronizer.SynchronizeAsync(config, SyncMode.Incremental, Arg.Any<CancellationToken>()).Returns(Result(SyncOutcome.Published));
        var options = new GovernanceSyncOptions();
        options.Sources.Add(Seed);

        await RunOneCycleAsync(Worker(options));

        await _registry.Received(1).UpsertAsync(config, Arg.Any<CancellationToken>());
        await _synchronizer.Received(1).SynchronizeAsync(config, SyncMode.Incremental, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_failing_source_does_not_stop_the_others()
    {
        var bad = new GovernanceSourceConfig("bad", "https://git.example.test/bad.git", "main", string.Empty);
        var good = new GovernanceSourceConfig("good", "https://git.example.test/good.git", "main", string.Empty);
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns([bad, good]);
        _synchronizer.SynchronizeAsync(bad, Arg.Any<SyncMode>(), Arg.Any<CancellationToken>()).Returns<SyncResult>(_ => throw new InvalidOperationException("unreachable"));
        _synchronizer.SynchronizeAsync(good, Arg.Any<SyncMode>(), Arg.Any<CancellationToken>()).Returns(Result(SyncOutcome.UpToDate));

        await RunOneCycleAsync(Worker(new GovernanceSyncOptions()));

        await _synchronizer.Received(1).SynchronizeAsync(good, SyncMode.Incremental, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_runs_while_the_schema_is_not_ready()
    {
        _gate.IsReadyAsync(Arg.Any<CancellationToken>()).Returns(false);
        var options = new GovernanceSyncOptions();
        options.Sources.Add(Seed);

        await RunOneCycleAsync(Worker(options));

        await _registry.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
        await _registry.DidNotReceiveWithAnyArgs().ListAsync(default);
    }

    [Fact]
    public async Task A_disabled_worker_touches_nothing()
    {
        await RunOneCycleAsync(Worker(new GovernanceSyncOptions { Enabled = false }));

        await _registry.DidNotReceiveWithAnyArgs().ListAsync(default);
        await _synchronizer.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!, default, default);
    }

    [Fact]
    public async Task Sources_are_checked_again_on_every_interval()
    {
        var config = new GovernanceSourceConfig("acme", Seed.RepositoryUrl, "main", string.Empty);
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns([config]);
        _synchronizer.SynchronizeAsync(config, SyncMode.Incremental, Arg.Any<CancellationToken>()).Returns(Result(SyncOutcome.UpToDate));
        var worker = Worker(new GovernanceSyncOptions { Interval = TimeSpan.FromMinutes(1) });

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        await _synchronizer.Received(2).SynchronizeAsync(config, SyncMode.Incremental, Arg.Any<CancellationToken>());
    }
}
