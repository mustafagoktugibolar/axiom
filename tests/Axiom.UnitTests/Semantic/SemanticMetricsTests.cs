using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Semantic;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Semantic;

public class SemanticMetricsTests
{
    private const string Code = SemanticFindingCodes.PotentialDecisionConflict;
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeFeedback : ISemanticFeedbackStore
    {
        public List<SemanticFindingObservation> Raised { get; } = [];

        public List<SemanticFeedback> Overturns { get; } = [];

        public Task<ImmutableArray<SemanticFindingObservation>> ListRaisedAsync(string organizationId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(Raised.Where(r => r.RaisedAt >= since && r.RaisedAt < until).ToImmutableArray());

        public Task<ImmutableArray<SemanticFeedback>> ListOverturnsAsync(string organizationId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(Overturns.Where(o => o.DecidedAt >= since && o.DecidedAt < until).ToImmutableArray());
    }

    private static SemanticFindingObservation Raised(double confidence, string model = "m1", string provider = "anthropic", string code = Code, DateTimeOffset? at = null) =>
        new("ev", code, provider, model, confidence, ["ARCH-1"], at ?? Now.AddDays(-1));

    private static SemanticFeedback Overturned(double confidence, string model = "m1", string provider = "anthropic") =>
        new("rv", "ev", Code, provider, model, confidence, ["ARCH-1"], "user:bob", "Not a conflict, see ADR.", Now.AddHours(-1));

    private static SemanticMetricsService Service(FakeFeedback feedback, SemanticCalibrationOptions? calibration = null) =>
        new(feedback, Options.Create(new SemanticOptions()), Options.Create(calibration ?? new SemanticCalibrationOptions { MinSamples = 5 }), new FakeTimeProvider(Now));

    private static AxiomPrincipal Principal(Role role) => new("user:amy", null, "acme", [role], []);

    [Fact]
    public async Task Counts_and_rates_are_per_code_provider_and_model()
    {
        var feedback = new FakeFeedback();
        feedback.Raised.AddRange([Raised(0.6), Raised(0.6), Raised(0.9), Raised(0.9), Raised(0.7, model: "m2")]);
        feedback.Overturns.AddRange([Overturned(0.6), Overturned(0.6), Overturned(0.9)]);

        var report = await Service(feedback).GetAsync(Principal(Role.Auditor), null, null, default);

        var m1 = Assert.Single(report.Groups, g => g.Model == "m1");
        Assert.Equal((4, 3, 0.75), (m1.Raised, m1.Overturned, m1.OverturnRate));
        var m2 = Assert.Single(report.Groups, g => g.Model == "m2");
        Assert.Equal((1, 0, 0.0), (m2.Raised, m2.Overturned, m2.OverturnRate));
        Assert.Equal((5, 3), (report.Raised, report.Overturned));
    }

    [Fact]
    public async Task Confidence_bands_split_the_rates()
    {
        var feedback = new FakeFeedback();
        feedback.Raised.AddRange([Raised(0.6), Raised(0.65), Raised(0.95), Raised(1.0)]);
        feedback.Overturns.AddRange([Overturned(0.6), Overturned(0.65)]);

        var report = await Service(feedback).GetAsync(Principal(Role.Auditor), null, null, default);

        var low = Assert.Single(report.Bands, b => b.Lower == 0.6);
        var top = Assert.Single(report.Bands, b => b.Lower == 0.9);
        Assert.Equal((2, 2, 1.0), (low.Raised, low.Overturned, low.OverturnRate));
        Assert.Equal((2, 0, 0.0), (top.Raised, top.Overturned, top.OverturnRate));
    }

    [Fact]
    public async Task The_window_is_applied()
    {
        var feedback = new FakeFeedback();
        feedback.Raised.AddRange([Raised(0.9), Raised(0.9, at: Now.AddDays(-90))]);

        var report = await Service(feedback).GetAsync(Principal(Role.Auditor), null, null, default);

        Assert.Equal(1, report.Raised);
    }

    [Fact]
    public async Task Reading_metrics_needs_audit_access_and_a_valid_window()
    {
        var service = Service(new FakeFeedback());

        await Assert.ThrowsAsync<AxiomException>(() => service.GetAsync(Principal(Role.Contributor), null, null, default));
        await Assert.ThrowsAsync<AxiomException>(() => service.GetAsync(Principal(Role.Auditor), Now, Now.AddDays(-1), default));
        await Assert.ThrowsAsync<AxiomException>(() => service.GetAsync(Principal(Role.Auditor), Now.AddDays(-400), Now, default));
    }

    [Fact]
    public void Provider_and_model_come_from_the_model_evidence()
    {
        var finding = new Finding
        {
            Code = Code, Severity = EnforcementLevel.Warn, Source = FindingSource.Semantic, Message = "m",
            Evidence = [new EvidenceRef("decision", "ARCH-1"), new EvidenceRef("model", "anthropic/claude-x/1")],
        };

        Assert.Equal(("anthropic", "claude-x/1"), SemanticFindingInfo.ProviderOf(finding));
        Assert.Equal(("unknown", "unknown"), SemanticFindingInfo.ProviderOf(finding with { Evidence = [] }));
    }

    private static ConfidenceBand Band(double lower, int raised, int overturned) => new(lower, lower + 0.1, raised, overturned);

    [Fact]
    public void Calibrator_suggests_the_lowest_threshold_that_meets_the_target()
    {
        var options = new SemanticCalibrationOptions { TargetOverturnRate = 0.25, ReviewTargetOverturnRate = 0.1, MinSamples = 10 };
        ConfidenceBand[] bands = [Band(0.5, 20, 16), Band(0.6, 20, 10), Band(0.7, 20, 4), Band(0.8, 20, 2), Band(0.9, 20, 1)];

        var suggestion = ConfidenceCalibrator.Suggest(bands, 0.5, 0.8, options);

        Assert.True(suggestion.HasSuggestion);
        Assert.Equal(0.6, suggestion.MinConfidence);
        Assert.Equal(0.8, suggestion.ReviewConfidence);
        Assert.Equal((0.5, 0.8), (suggestion.CurrentMinConfidence, suggestion.CurrentReviewConfidence));
    }

    [Fact]
    public void Calibrator_declines_without_enough_data_or_when_no_threshold_works()
    {
        var options = new SemanticCalibrationOptions { MinSamples = 30 };

        Assert.False(ConfidenceCalibrator.Suggest([Band(0.5, 10, 1)], 0.5, 0.8, options).HasSuggestion);
        Assert.False(ConfidenceCalibrator.Suggest([Band(0.5, 40, 40), Band(0.9, 40, 30)], 0.5, 0.8, options).HasSuggestion);
        Assert.False(ConfidenceCalibrator.Suggest([], 0.5, 0.8, options).HasSuggestion);
    }

    [Fact]
    public void Calibrator_never_suggests_review_below_min()
    {
        var options = new SemanticCalibrationOptions { TargetOverturnRate = 0.5, ReviewTargetOverturnRate = 0.6, MinSamples = 5 };

        var suggestion = ConfidenceCalibrator.Suggest([Band(0.5, 10, 5), Band(0.9, 10, 1)], 0.5, 0.8, options);

        Assert.True(suggestion.ReviewConfidence >= suggestion.MinConfidence);
    }

    [Fact]
    public async Task The_report_carries_a_suggestion_but_changes_nothing()
    {
        var feedback = new FakeFeedback();
        for (var i = 0; i < 10; i++)
        {
            feedback.Raised.Add(Raised(0.55));
            feedback.Raised.Add(Raised(0.95));
        }

        feedback.Overturns.AddRange(Enumerable.Range(0, 8).Select(_ => Overturned(0.55)));
        var options = new SemanticOptions();

        var report = await new SemanticMetricsService(feedback, Options.Create(options), Options.Create(new SemanticCalibrationOptions { MinSamples = 5 }), new FakeTimeProvider(Now))
            .GetAsync(Principal(Role.Auditor), null, null, default);

        Assert.True(report.Suggestion.HasSuggestion);
        Assert.Equal(0.9, report.Suggestion.MinConfidence);
        Assert.Equal((0.5, 0.8), (options.MinConfidence, options.ReviewConfidence));
    }
}
