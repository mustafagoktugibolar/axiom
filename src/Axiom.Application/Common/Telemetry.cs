using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Axiom.Application.Common;

/// <summary>
/// Trace and metric instruments (design.md §15). Labels never carry task text, source content or
/// other sensitive values (observability-and-slo.md).
/// </summary>
public static class AxiomTelemetry
{
    public const string Name = "Axiom";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Evaluations = Meter.CreateCounter<long>("axiom.evaluations", description: "Evaluations by stage and verdict.");
    public static readonly Histogram<double> EvaluationDuration = Meter.CreateHistogram<double>("axiom.evaluation.duration", "ms", "Evaluation latency by stage.");
    public static readonly Counter<long> EvaluationCacheHits = Meter.CreateCounter<long>("axiom.evaluation.cache_hits", description: "Evaluations answered from an identical earlier evaluation.");
    public static readonly Counter<long> DeterministicViolations = Meter.CreateCounter<long>("axiom.policy.violations", description: "Deterministic rule violations by rule.");
    public static readonly Counter<long> ResolutionConflicts = Meter.CreateCounter<long>("axiom.resolution.conflicts", description: "Authority conflicts detected during resolution.");
    public static readonly Histogram<long> ResolutionContextSize = Meter.CreateHistogram<long>("axiom.resolution.context_size", "records", "Number of governance records returned per resolution.");
    public static readonly Counter<long> SemanticFindings = Meter.CreateCounter<long>("axiom.semantic.findings", description: "Semantic findings by type and severity.");
    public static readonly Counter<long> SemanticOverturns = Meter.CreateCounter<long>("axiom.semantic.overturns", description: "Semantic findings overturned by a reviewer.");
    public static readonly Counter<long> SemanticTokens = Meter.CreateCounter<long>("axiom.semantic.tokens", description: "Model tokens consumed.");
    public static readonly Counter<long> ReceiptFailures = Meter.CreateCounter<long>("axiom.receipt.failures", description: "Receipt generation failures.");
    public static readonly Counter<long> ScmStatusFailures = Meter.CreateCounter<long>("axiom.scm.status_failures", description: "Status checks that could not be published to the source-control system.");
    public static readonly Counter<long> ExceptionUsage = Meter.CreateCounter<long>("axiom.exception.usage", description: "Exceptions applied during evaluations.");
    public static readonly Histogram<double> IngestionLag = Meter.CreateHistogram<double>("axiom.ingestion.lag", "s", "Delay between a governance commit and its published snapshot.");

    public static class Spans
    {
        public const string Preflight = "axiom.preflight";
        public const string ResolveScope = "axiom.resolve_scope";
        public const string ResolveGovernance = "axiom.resolve_governance";
        public const string PolicyRun = "axiom.policy.run";
        public const string SemanticAnalyze = "axiom.semantic.analyze";
        public const string DesignValidate = "axiom.design.validate";
        public const string DiffValidate = "axiom.diff.validate";
        public const string ReceiptEmit = "axiom.receipt.emit";
    }
}
