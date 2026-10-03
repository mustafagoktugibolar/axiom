using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Domain.Audit;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;
using Axiom.Infrastructure.Audit;

namespace Axiom.UnitTests.Evaluation;

public class EvaluationDocumentTests
{
    [Fact]
    public void Stored_document_round_trips_to_an_identical_receipt_payload()
    {
        var evaluation = new EvaluationRecord
        {
            Id = "ev_1",
            OrganizationId = "acme",
            Stage = EvaluationStage.PullRequest,
            CreatedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            Actor = new ActorIdentity("user:alice", "Alice", "claude-code", "s1"),
            Scm = new ScmCoordinates("repo:gateway", "feature/x", "abc1234", "def5678", "42"),
            Task = "Change routing",
            RequestFingerprint = "fp",
            SnapshotId = "gs_1",
            SourceCommit = "c1",
            SnapshotPublishedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            CatalogVersion = "cv_1",
            ParentEvaluationId = "ev_0",
            DesignId = "DESIGN-101",
            DesignHash = "dh",
            ResolvedScope = new ResolvedScopeView(
                ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty.Add("repository", ["repo:gateway"]), ["no owner"]),
            AppliedRecords = [new AppliedRecordRef("ARCH-042", 7, "h1", RecordKind.Decision, "Gateway", Importance.Required, true, [], ["EXC-023"], [], [])],
            AppliedExceptions = [new AppliedExceptionRef("EXC-023", 2, "h2", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), Containment.Partial, ["ARCH-042"])],
            RequiredChecks = [new RequiredCheck("forbidden-import", "ARCH-042", EnforcementLevel.Block)],
            PolicyRuns = [new PolicyRunResult("forbidden-import", "1.0.0", "rh", "ARCH-042", EnforcementLevel.Block, PolicyOutcome.Violated, [new PolicyViolation("bad import", "src/a.cs", 3, "using X;")])],
            Findings =
            [
                new Finding
                {
                    Code = "POLICY_VIOLATION", Severity = EnforcementLevel.Block, Source = FindingSource.Policy, Message = "bad import",
                    GovernanceIds = ["ARCH-042"], Evidence = [new EvidenceRef("decision", "ARCH-042", "7", "governance/decisions/ARCH-042.md", "h1")],
                    RecommendedAction = "Remove it", Path = "src/a.cs", Line = 3, RuleId = "forbidden-import", RuleVersion = "1.0.0", WaivedBy = "EXC-023",
                },
            ],
            Significance = new SignificanceAssessment(true, [new SignificanceTrigger("PUBLIC_CONTRACT", "public-contract", "x", ["path"], null)], ["public-contract"]),
            RequiredActions = ["Do it"],
            RequiredReviewers = ["platform-architecture"],
            AffectedDependencies = [new AffectedDependency("component:gui/portal", 1, ["a -DependsOn-> b"], ["team:gui"], true)],
            SemanticCoverage = new SemanticCoverage(SemanticCoverageStatus.Degraded, "provider down", "anthropic", "claude"),
            Trace = [new TraceEntry("ARCH-042", 7, RecordKind.Decision, true, ["status=accepted"], [0, 1, 0])],
            Verdict = Verdict.Block,
        };

        var json = JsonSerializer.Serialize(evaluation, EfEvaluationStore.DocumentOptions);
        var restored = JsonSerializer.Deserialize<EvaluationRecord>(json, EfEvaluationStore.DocumentOptions)!;

        Assert.Equal(ReceiptFactory.CanonicalPayload(evaluation), ReceiptFactory.CanonicalPayload(restored));
        Assert.Equal(json, JsonSerializer.Serialize(restored, EfEvaluationStore.DocumentOptions));
        Assert.Equal(evaluation.Trace[0].Specificity.AsEnumerable(), restored.Trace[0].Specificity);
        Assert.Equal(evaluation.Findings[0].Evidence.AsEnumerable(), restored.Findings[0].Evidence);
    }
}
