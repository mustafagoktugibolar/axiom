using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Axiom.Application.Common;

namespace Axiom.UnitTests.Observability;

/// <summary>
/// Task 12.9 contract: the dashboard and the alert rules under deploy/observability may only reference
/// metrics that Telemetry.cs really defines, spelled the way the OTLP -> Prometheus translation spells
/// them (counter '_total', unit suffix, histogram '_bucket/_sum/_count').
/// </summary>
public partial class MetricContractTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Observability");

    private static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    [GeneratedRegex(@"\baxiom_[a-z0-9_]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetricToken();

    private static string UnitSuffix(string? unit) => unit switch
    {
        null or "" => string.Empty,
        "ms" => "_milliseconds",
        "s" => "_seconds",
        var other => "_" + other,
    };

    /// <summary>Every Prometheus series name the instruments in Telemetry.cs can produce.</summary>
    private static HashSet<string> ExportedSeries()
    {
        var series = new HashSet<string>(StringComparer.Ordinal);
        var instruments = typeof(AxiomTelemetry).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<Instrument>()
            .ToList();
        Assert.NotEmpty(instruments);

        foreach (var instrument in instruments)
        {
            var baseName = instrument.Name.Replace('.', '_') + UnitSuffix(instrument.Unit);
            var type = instrument.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Counter<>))
            {
                series.Add(baseName + "_total");
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Histogram<>))
            {
                series.Add(baseName + "_bucket");
                series.Add(baseName + "_sum");
                series.Add(baseName + "_count");
            }
            else
            {
                series.Add(baseName);
            }
        }

        return series;
    }

    public static TheoryData<string> Files => ["axiom-alerts.yaml", "axiom-slo-dashboard.json"];

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_metric_the_artifact_references_exists_in_telemetry(string file)
    {
        var referenced = MetricToken().Matches(Read(file)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var exported = ExportedSeries();

        Assert.NotEmpty(referenced);
        var unknown = referenced.Where(r => !exported.Contains(r)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unknown.Length == 0, $"{file} references metrics that Telemetry.cs does not define: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void The_artifacts_cover_the_slo_signals_that_have_instruments()
    {
        var all = Read("axiom-alerts.yaml") + Read("axiom-slo-dashboard.json");

        foreach (var series in new[]
        {
            "axiom_evaluation_duration_milliseconds_bucket", "axiom_evaluations_total", "axiom_receipt_failures_total",
            "axiom_ingestion_lag_seconds_bucket", "axiom_scm_status_failures_total", "axiom_semantic_overturns_total",
        })
        {
            Assert.Contains(series, all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Latency_thresholds_match_the_documented_slo_targets()
    {
        var alerts = Read("axiom-alerts.yaml");

        // p95 preflight < 1.5 s, semantic < 5 s, PR evaluation < 30 s (milliseconds), projection lag < 60 s.
        Assert.Matches(@"Preflight""\}\[10m\]\)\)\) > 1500\b", alerts);
        Assert.Matches(@"stage=""Diff""\}\[30m\]\)\)\) > 5000\b", alerts);
        Assert.Matches(@"stage=""PullRequest""\}\[30m\]\)\)\) > 30000\b", alerts);
        Assert.Matches(@"axiom_ingestion_lag_seconds_bucket\[15m\]\)\)\) > 60\b", alerts);
    }

    [Fact]
    public void Alert_rules_are_well_formed()
    {
        var alerts = Read("axiom-alerts.yaml");

        Assert.Contains("kind: PrometheusRule", alerts, StringComparison.Ordinal);
        var names = Regex.Matches(alerts, @"^\s+- alert: (\w+)", RegexOptions.Multiline, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(names.Length, Regex.Count(alerts, @"^\s+expr: ", RegexOptions.Multiline, TimeSpan.FromSeconds(1)));
        Assert.Equal(names.Length, Regex.Count(alerts, @"^\s+summary: ", RegexOptions.Multiline, TimeSpan.FromSeconds(1)));
        Assert.Contains("AxiomReceiptGenerationFailing", names);
    }

    [Fact]
    public void The_dashboard_is_valid_json_with_a_query_on_every_panel()
    {
        using var document = JsonDocument.Parse(Read("axiom-slo-dashboard.json"));
        var root = document.RootElement;

        Assert.Equal("axiom-slo", root.GetProperty("uid").GetString());
        var panels = root.GetProperty("panels").EnumerateArray().Where(p => p.GetProperty("type").GetString() != "row").ToList();
        Assert.NotEmpty(panels);
        var ids = root.GetProperty("panels").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var panel in panels)
        {
            var targets = panel.GetProperty("targets").EnumerateArray().ToList();
            Assert.NotEmpty(targets);
            Assert.All(targets, t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("expr").GetString())));
        }
    }

    [Fact]
    public void Metric_labels_never_carry_sensitive_free_text()
    {
        // observability-and-slo.md: no task or source contents in labels. The instruments are tagged only with
        // these keys; a new key must be reviewed.
        var allowed = new[] { "stage", "verdict", "code", "severity", "provider" };
        var usedInQueries = Regex.Matches(Read("axiom-alerts.yaml") + Read("axiom-slo-dashboard.json"), @"by \((\w+)", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Groups[1].Value)
            .Where(g => g != "le");

        Assert.All(usedInQueries, label => Assert.Contains(label, allowed));
    }
}
