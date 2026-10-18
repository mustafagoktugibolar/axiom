using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Policy;
using Axiom.Application.Query;
using Axiom.Application.Review;
using Axiom.Domain.Audit;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Review;
using Axiom.UnitTests.Evaluation;
using Axiom.UnitTests.Governance;
using Axiom.UnitTests.Policy;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Audit;

/// <summary>
/// Task 12.8: stored evaluation inputs replayed through the pipeline with a fixed clock must reproduce
/// the same evaluation IDs, verdicts, findings and receipt hashes, and altering any stored receipt
/// field must break chain verification.
/// </summary>
public class ReceiptReplayTests
{
    private const string Org = "acme";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(string Head, ChangedFile[] Files);

    private static readonly Fixture[] Inputs =
    [
        new("bbbbbbb1", [PolicyTestKit.Added("src/Generated/Client.cs", "class Client;"), PolicyTestKit.Added("src/Program.cs", "class Program;")]),
        new("bbbbbbb2", [PolicyTestKit.Added("src/Program.cs", "class Program;")]),
        new("bbbbbbb3", [PolicyTestKit.Added("src/Generated/Other.cs", "class Other;"), PolicyTestKit.Added("README.md", "# readme")]),
    ];

    private sealed class Rig
    {
        public Rig(DateTimeOffset now)
        {
            Time = new FakeTimeProvider(now);
            var graph = new FakeGraph();
            graph.Bind(new RepositoryTopology(EntityRef.Parse("repo:gateway"), [EntityRef.Parse("component:gui/api-gateway")], [EntityRef.Parse("system:gui-platform")],
                [], [], [], [], [EntityRef.Parse("team:platform")], [], []));
            Snapshots.Publish(Org,
                TestRecords.Decision("ARCH-100", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
                    rules: [PolicyTestKit.Bind("forbidden-path-change", EnforcementLevel.Block, ("paths", "src/Generated/**"))]));
            Service = new DiffEvaluationService(
                new EvaluationPipeline(Snapshots, new ChangeScopeResolver(graph), graph, Store, Time),
                new FixedScm(this),
                new PolicyEngine(PolicyRuleCatalog.BuiltIn),
                PolicyRuleCatalog.BuiltIn,
                Store,
                new NoReviews());
        }

        public FakeTimeProvider Time { get; }

        public FakeSnapshots Snapshots { get; } = new();

        public InMemoryEvaluationStore Store { get; } = new();

        public DiffEvaluationService Service { get; }

        public ScmDiff Diff { get; set; } = new([], InMemoryRepositoryView.Empty, false, 0);

        public async Task<StoredEvaluation> RunAsync(Fixture input, bool reverse = false)
        {
            var files = reverse ? input.Files.Reverse().ToArray() : input.Files;
            Diff = new ScmDiff([.. files], InMemoryRepositoryView.FromChanges(files), false, files.Length);
            return await Service.ExecuteAsync(Principals.With(Org, Role.Contributor),
                new DiffValidationRequest(Org, "gateway", "aaaaaaa1", input.Head, "feature/x", null, null, "kiro", "s1"), default);
        }
    }

    private static async Task<List<StoredEvaluation>> ReplayAsync(Rig rig, bool reverse = false)
    {
        var results = new List<StoredEvaluation>();
        foreach (var input in Inputs)
        {
            results.Add(await rig.RunAsync(input, reverse));
        }

        return results;
    }

    private static string[] FindingKeys(EvaluationRecord e) =>
        [.. e.Findings.Select(f => $"{f.Code}|{f.Severity}|{f.Path}|{f.Line}|{string.Join(',', f.GovernanceIds)}|{f.RuleId}|{f.Message}")];

    [Fact]
    public async Task Replaying_the_same_inputs_in_a_fresh_system_reproduces_ids_verdicts_findings_and_receipt_hashes()
    {
        var first = await ReplayAsync(new Rig(Now));
        var second = await ReplayAsync(new Rig(Now));

        Assert.Equal(first.Count, second.Count);
        Assert.Equal([Verdict.Block, Verdict.Allow, Verdict.Block], first.Select(s => s.Evaluation.Verdict));
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Evaluation.Id, second[i].Evaluation.Id);
            Assert.Equal(first[i].Evaluation.Verdict, second[i].Evaluation.Verdict);
            Assert.Equal(FindingKeys(first[i].Evaluation), FindingKeys(second[i].Evaluation));
            Assert.Equal(first[i].Receipt.Payload, second[i].Receipt.Payload);
            Assert.Equal(first[i].Receipt.Id, second[i].Receipt.Id);
            Assert.Equal(first[i].Receipt.Digest, second[i].Receipt.Digest);
            Assert.Equal(first[i].Receipt.ChainDigest, second[i].Receipt.ChainDigest);
        }

        Assert.Equal(first.Count, first.Select(s => s.Evaluation.Id).Distinct().Count());
    }

    [Fact]
    public async Task The_order_in_which_the_scm_lists_changed_files_does_not_change_the_result()
    {
        var forward = await ReplayAsync(new Rig(Now));
        var reversed = await ReplayAsync(new Rig(Now), reverse: true);

        for (var i = 0; i < forward.Count; i++)
        {
            Assert.Equal(forward[i].Evaluation.Id, reversed[i].Evaluation.Id);
            Assert.Equal(forward[i].Receipt.Digest, reversed[i].Receipt.Digest);
        }
    }

    [Fact]
    public async Task Asking_again_in_the_same_system_returns_the_stored_receipt_unchanged()
    {
        var rig = new Rig(Now);
        var first = await rig.RunAsync(Inputs[0]);
        rig.Time.Advance(TimeSpan.FromHours(3));

        var again = await rig.RunAsync(Inputs[0]);

        Assert.Equal(first.Evaluation.Id, again.Evaluation.Id);
        Assert.Equal(first.Receipt, again.Receipt);
        Assert.Single(rig.Store.Items);
    }

    [Fact]
    public async Task A_later_replay_keeps_the_evaluation_id_but_the_receipt_hash_follows_the_issue_time()
    {
        // The ID is stable within a validity epoch; the receipt records when it was issued, so its hash is
        // reproducible only for a fixed clock. This is by design (see threat-model-review.md, finding R-3).
        var early = await new Rig(Now).RunAsync(Inputs[0]);
        var late = await new Rig(Now.AddHours(5)).RunAsync(Inputs[0]);

        Assert.Equal(early.Evaluation.Id, late.Evaluation.Id);
        Assert.Equal(early.Evaluation.Verdict, late.Evaluation.Verdict);
        Assert.Equal(FindingKeys(early.Evaluation), FindingKeys(late.Evaluation));
        Assert.NotEqual(early.Receipt.Digest, late.Receipt.Digest);
    }

    [Fact]
    public async Task A_change_of_governance_input_changes_the_evaluation_id()
    {
        var baseline = await new Rig(Now).RunAsync(Inputs[1]);
        var rig = new Rig(Now);
        rig.Snapshots.Publish(Org,
            TestRecords.Decision("ARCH-100", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
                rules: [PolicyTestKit.Bind("forbidden-path-change", EnforcementLevel.Warn, ("paths", "src/Generated/**"))]) with { ContentHash = "hash-ARCH-100-edited" });

        var changed = await rig.RunAsync(Inputs[1]);

        Assert.NotEqual(baseline.Evaluation.Id, changed.Evaluation.Id);
    }

    [Fact]
    public async Task The_persisted_document_round_trips_to_the_exact_payload_the_receipt_covers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        foreach (var stored in await ReplayAsync(new Rig(Now)))
        {
            var document = JsonSerializer.Serialize(stored.Evaluation, options);
            var reloaded = JsonSerializer.Deserialize<EvaluationRecord>(document, options)!;

            Assert.Equal(stored.Receipt.Payload, ReceiptFactory.CanonicalPayload(reloaded));
            Assert.True(ReceiptFactory.Matches(reloaded, stored.Receipt));
            Assert.Equal(ReceiptFactory.Create(reloaded, stored.Receipt.PreviousChainDigest), stored.Receipt);
        }
    }

    [Fact]
    public async Task Altering_the_evaluation_document_no_longer_matches_its_receipt()
    {
        var stored = (await ReplayAsync(new Rig(Now)))[0];
        var evaluation = stored.Evaluation;

        Assert.True(ReceiptFactory.Matches(evaluation, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { Verdict = Verdict.Allow }, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { Findings = [] }, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { Findings = [.. evaluation.Findings.Select(f => f with { Message = f.Message + " (edited)" })] }, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { SnapshotId = "gs_other" }, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { Scm = evaluation.Scm with { CommitSha = "ccccccc9" } }, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { Actor = evaluation.Actor with { Subject = "user:mallory" } }, stored.Receipt));
        Assert.False(ReceiptFactory.Matches(evaluation with { Task = "something else" }, stored.Receipt));
    }

    public static TheoryData<string> TamperedFields =>
    [
        nameof(Receipt.Id), nameof(Receipt.OrganizationId), nameof(Receipt.EvaluationId), nameof(Receipt.Stage), nameof(Receipt.Verdict),
        nameof(Receipt.Repository), nameof(Receipt.CommitSha), nameof(Receipt.SnapshotId), nameof(Receipt.IssuedAt), nameof(Receipt.Payload),
        nameof(Receipt.Digest), nameof(Receipt.PreviousChainDigest), nameof(Receipt.ChainDigest),
    ];

    private static Receipt Tamper(Receipt r, string field) => field switch
    {
        nameof(Receipt.Id) => r with { Id = "rc_" + new string('a', 24) },
        nameof(Receipt.OrganizationId) => r with { OrganizationId = "other" },
        nameof(Receipt.EvaluationId) => r with { EvaluationId = "ev_forged" },
        nameof(Receipt.Stage) => r with { Stage = EvaluationStage.Preflight },
        nameof(Receipt.Verdict) => r with { Verdict = r.Verdict == Verdict.Allow ? Verdict.Block : Verdict.Allow },
        nameof(Receipt.Repository) => r with { Repository = "repo:other" },
        nameof(Receipt.CommitSha) => r with { CommitSha = "ccccccc9" },
        nameof(Receipt.SnapshotId) => r with { SnapshotId = "gs_forged" },
        nameof(Receipt.IssuedAt) => r with { IssuedAt = r.IssuedAt.AddSeconds(1) },
        nameof(Receipt.Payload) => r with { Payload = r.Payload.Replace("\"stage\"", "\"stag3\"", StringComparison.Ordinal) },
        nameof(Receipt.Digest) => r with { Digest = new string('b', 64) },
        nameof(Receipt.PreviousChainDigest) => r with { PreviousChainDigest = new string('c', 64) },
        nameof(Receipt.ChainDigest) => r with { ChainDigest = new string('d', 64) },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    [Theory]
    [MemberData(nameof(TamperedFields))]
    public async Task Altering_any_stored_receipt_field_breaks_chain_verification_at_that_receipt(string field)
    {
        var chain = (await ReplayAsync(new Rig(Now))).Select(s => s.Receipt).ToList();
        Assert.True((await VerifyAsync(chain)).Intact);

        for (var target = 0; target < chain.Count; target++)
        {
            var copy = chain.ToList();
            copy[target] = Tamper(copy[target], field);

            var result = await VerifyAsync(copy);

            Assert.False(result.Intact, $"tampering with {field} of receipt {target} went undetected");
            Assert.Equal(copy[target].Id, result.FirstBrokenReceiptId);
        }
    }

    [Fact]
    public async Task Removing_or_reordering_receipts_breaks_the_chain()
    {
        var chain = (await ReplayAsync(new Rig(Now))).Select(s => s.Receipt).ToList();

        Assert.False((await VerifyAsync([chain[0], chain[2]])).Intact);
        Assert.False((await VerifyAsync([chain[1], chain[2]])).Intact);
        Assert.False((await VerifyAsync([chain[1], chain[0], chain[2]])).Intact);
    }

    private static Task<ChainVerificationDto> VerifyAsync(IReadOnlyList<Receipt> chain) =>
        new ReceiptService(new ChainStore(chain), new FakeGraph()).VerifyChainAsync(Principals.With(Org, Role.Auditor), default);

    private sealed class ChainStore(IReadOnlyList<Receipt> chain) : IEvaluationStore
    {
        public async IAsyncEnumerable<Receipt> ReadChainAsync(string organizationId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var receipt in chain)
            {
                await Task.Yield();
                yield return receipt;
            }
        }

        public Task<StoredEvaluation?> FindAsync(string organizationId, string evaluationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoredEvaluation> AppendAsync(EvaluationRecord evaluation, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Receipt?> FindReceiptAsync(string organizationId, string receiptOrEvaluationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Receipt?> FindReceiptByCommitAsync(string organizationId, string repository, string commitSha, EvaluationStage? stage, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(ImmutableArray<EvaluationSummary> Items, int Total)> SearchAsync(string organizationId, EvaluationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedScm(Rig rig) : IScmDiffSource
    {
        public Task<ScmDiff> GetDiffAsync(ScmDiffRequest request, CancellationToken cancellationToken) => Task.FromResult(rig.Diff);
    }

    private sealed class NoReviews : IReviewStore
    {
        public Task AppendAsync(ReviewDecision decision, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ImmutableArray<ReviewDecision>> ListAsync(string organizationId, string evaluationId, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<ReviewDecision>.Empty);

        public Task<ImmutableArray<ReviewQueueItem>> ListPendingAsync(string organizationId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<ReviewQueueItem>.Empty);
    }
}
