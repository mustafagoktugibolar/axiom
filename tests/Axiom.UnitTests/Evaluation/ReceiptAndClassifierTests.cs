using System.Collections.Immutable;
using Axiom.Domain.Audit;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.UnitTests.Governance;

namespace Axiom.UnitTests.Evaluation;

public class ReceiptAndClassifierTests
{
    private static EvaluationRecord Evaluation(Verdict verdict = Verdict.Allow, params Finding[] findings) => new()
    {
        Id = "ev_1",
        OrganizationId = "acme",
        Stage = EvaluationStage.Diff,
        CreatedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
        Actor = new ActorIdentity("user:alice", "Alice", "kiro", "s1"),
        Scm = new ScmCoordinates("repo:gateway", "main", "abc1234", "def5678", "42"),
        Task = "Change routing",
        RequestFingerprint = "fp",
        SnapshotId = "gs_1",
        SourceCommit = "c1",
        SnapshotPublishedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        CatalogVersion = "cv_1",
        ResolvedScope = new ResolvedScopeView(ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty, []),
        Significance = new SignificanceAssessment(false, [], []),
        Findings = [.. findings],
        Verdict = verdict,
    };

    private static Finding Finding(string code, string path) => new()
    {
        Code = code,
        Severity = EnforcementLevel.Block,
        Source = FindingSource.Policy,
        Message = "violation",
        Path = path,
        GovernanceIds = ["ARCH-042"],
    };

    [Fact]
    public void Receipt_is_content_addressed_and_verifiable()
    {
        var receipt = ReceiptFactory.Create(Evaluation(), ReceiptFactory.GenesisDigest);

        Assert.True(ReceiptFactory.Verify(receipt));
        Assert.Equal("rc_" + receipt.Digest[..24], receipt.Id);
        Assert.Equal(receipt.Digest, ReceiptFactory.Create(Evaluation(), ReceiptFactory.GenesisDigest).Digest);
        Assert.DoesNotContain("Change routing", receipt.Payload, StringComparison.Ordinal);
        Assert.Contains("\"snapshotId\":\"gs_1\"", receipt.Payload, StringComparison.Ordinal);
        Assert.Contains("\"commitSha\":\"abc1234\"", receipt.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_changes_with_anything_that_determines_the_verdict()
    {
        var baseline = ReceiptFactory.Create(Evaluation(), ReceiptFactory.GenesisDigest).Digest;

        Assert.NotEqual(baseline, ReceiptFactory.Create(Evaluation(Verdict.Block), ReceiptFactory.GenesisDigest).Digest);
        Assert.NotEqual(baseline, ReceiptFactory.Create(Evaluation() with { SnapshotId = "gs_2" }, ReceiptFactory.GenesisDigest).Digest);
        Assert.NotEqual(baseline, ReceiptFactory.Create(Evaluation() with { Scm = new ScmCoordinates("repo:gateway", "main", "fff0000", null, null) }, ReceiptFactory.GenesisDigest).Digest);
        Assert.NotEqual(baseline, ReceiptFactory.Create(
            Evaluation() with { AppliedRecords = [new AppliedRecordRef("ARCH-042", 7, "h", RecordKind.Decision, "t", Axiom.Domain.Resolution.Importance.Required, false, [], [], [], [])] },
            ReceiptFactory.GenesisDigest).Digest);
    }

    [Fact]
    public void Digest_does_not_depend_on_finding_order()
    {
        var a = Finding("A", "x.cs");
        var b = Finding("B", "y.cs");

        Assert.Equal(
            ReceiptFactory.Create(Evaluation(Verdict.Block, a, b), ReceiptFactory.GenesisDigest).Digest,
            ReceiptFactory.Create(Evaluation(Verdict.Block, b, a), ReceiptFactory.GenesisDigest).Digest);
    }

    [Fact]
    public void Tampering_with_payload_or_chain_is_detected()
    {
        var first = ReceiptFactory.Create(Evaluation(), ReceiptFactory.GenesisDigest);
        var second = ReceiptFactory.Create(Evaluation() with { Id = "ev_2" }, first.ChainDigest);

        Assert.True(ReceiptFactory.Verify(second));
        Assert.False(ReceiptFactory.Verify(first with { Payload = first.Payload.Replace("ALLOW", "BLOCK", StringComparison.Ordinal) }));
        Assert.False(ReceiptFactory.Verify(second with { PreviousChainDigest = ReceiptFactory.GenesisDigest }));
        Assert.NotEqual(first.ChainDigest, second.ChainDigest);
    }

    [Theory]
    [InlineData(new[] { EnforcementLevel.Info }, Verdict.Allow)]
    [InlineData(new[] { EnforcementLevel.Info, EnforcementLevel.Warn }, Verdict.AllowWithWarnings)]
    [InlineData(new[] { EnforcementLevel.Warn, EnforcementLevel.RequireReview }, Verdict.RequireReview)]
    [InlineData(new[] { EnforcementLevel.RequireReview, EnforcementLevel.Block, EnforcementLevel.Info }, Verdict.Block)]
    [InlineData(new EnforcementLevel[0], Verdict.Allow)]
    public void Most_restrictive_finding_wins(EnforcementLevel[] severities, Verdict expected) =>
        Assert.Equal(expected, VerdictLattice.Combine(severities.Select(s => Finding("X", "p") with { Severity = s })));

    [Fact]
    public void Waived_findings_do_not_count_towards_the_verdict() =>
        Assert.Equal(Verdict.AllowWithWarnings, VerdictLattice.Combine(
        [
            Finding("X", "p") with { WaivedBy = "EXC-023" },
            Finding("Y", "p") with { Severity = EnforcementLevel.Warn },
        ]));

    [Theory]
    [InlineData("Fix a typo in the readme", new[] { "README.md" }, false, "docs-only")]
    [InlineData("Add unit tests", new[] { "tests/Unit/FooTests.cs" }, false, "tests-only")]
    [InlineData("Rename a local variable", new[] { "src/Foo.cs" }, false, null)]
    [InlineData("Update routing", new[] { "db/migrations/0042_add_column.sql" }, true, "data-model")]
    [InlineData("Update the client", new[] { "api/openapi.yaml" }, true, "public-contract")]
    [InlineData("Tune limits", new[] { "deploy/helm/values.yaml" }, true, "infrastructure")]
    [InlineData("Migrate sessions and switch to Redis", new string[0], true, "strategic-technology")]
    [InlineData("The immigrated user list is wrong", new string[0], false, null)]
    public void Classifier_applies_the_default_triggers(string task, string[] paths, bool significant, string? expectedClass)
    {
        var assessment = SignificantChangeClassifier.Classify(new SignificanceInput(task, [.. paths], 1, 1, false, []));

        Assert.Equal(significant, assessment.IsSignificant);
        if (expectedClass is not null)
        {
            Assert.Contains(expectedClass, assessment.ChangeClasses);
        }
    }

    [Fact]
    public void Decision_owner_policy_can_add_triggers_through_a_governance_record()
    {
        var policy = TestRecords.Decision("STD-010", rules:
        [
            new RuleBinding(SignificantChangeClassifier.TriggerRuleId, EnforcementLevel.Info,
                ImmutableSortedDictionary<string, string>.Empty.Add("paths", "src/Billing/**").Add("code", "BILLING_CORE")),
        ]);

        var assessment = SignificantChangeClassifier.Classify(new SignificanceInput(
            "Adjust rounding", ["src/Billing/Rounding.cs"], 1, 1, false, SignificantChangeClassifier.TriggersFrom([policy])));

        var trigger = Assert.Single(assessment.Triggers);
        Assert.Equal(("BILLING_CORE", "STD-010", ChangeClasses.OwnerPolicy), (trigger.Code, trigger.SourceRecordId, trigger.ChangeClass));
    }

    [Fact]
    public void Touching_several_systems_is_significant()
    {
        var assessment = SignificantChangeClassifier.Classify(new SignificanceInput("Small change", ["src/a.cs"], 2, 1, false, []));
        Assert.Contains(assessment.Triggers, t => t.Code == "MULTI_SYSTEM");
    }
}
