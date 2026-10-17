using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Axiom.Application.Common;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Axiom.Application.Semantic;

public sealed record SemanticInput(
    string Repository,
    string? Task,
    ResolutionResult Resolution,
    ImmutableArray<ChangedFile> Changes);

public sealed record SemanticOutcome(SemanticCoverage Coverage, ImmutableArray<Finding> Findings)
{
    public static SemanticOutcome NotRequested { get; } = new(SemanticCoverage.NotRequested, []);
}

/// <summary>
/// The bounded semantic analyzer (design.md §4.6). It runs only after deterministic resolution and
/// policy checks, and it can only add advisory findings: nothing a model says can lift a deterministic
/// failure (P9), and no semantic finding is ever BLOCK (ADR-0005). Provider output is untrusted: every
/// finding must cite a record and a file that were in the packet, or it is dropped.
/// </summary>
public sealed partial class SemanticAnalyzer(IOptions<SemanticOptions> options, ILogger<SemanticAnalyzer> logger, ISemanticProvider? provider = null)
{
    public const int MaxMessageChars = 600;

    private readonly SemanticOptions _options = options.Value;

    /// <summary>Identifies the analysis configuration so a change of provider, model or thresholds yields a new evaluation.</summary>
    public string Fingerprint => provider is null || !_options.Enabled
        ? "semantic:off"
        : string.Create(CultureInfo.InvariantCulture, $"semantic:{provider.Name}:{provider.Model}:{_options.MinConfidence}:{_options.ReviewConfidence}");

    public async Task<SemanticOutcome> AnalyzeAsync(SemanticInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!_options.Enabled || provider is null)
        {
            return SemanticOutcome.NotRequested;
        }

        // Task 7.12: a record whose scope forbids external LLMs keeps its change away from external providers.
        if (provider.IsExternal && input.Resolution.Applicable.FirstOrDefault(a => !a.Record.SemanticExternalAllowed) is { } forbidding)
        {
            return Outcome(SemanticCoverageStatus.ProhibitedByScope, $"Record {forbidding.Revision.Id} forbids sending this scope to an external model provider.");
        }

        var effective = input.Resolution.Effective.ToList();
        var packet = EvidencePacketBuilder.Build(input.Repository, input.Task, effective, input.Changes, _options);
        if (!packet.HasContent)
        {
            return Outcome(SemanticCoverageStatus.Complete, "Nothing to compare: no applicable decision text or no added lines.");
        }

        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.SemanticAnalyze);
        activity?.SetTag("axiom.semantic.provider", provider.Name);
        activity?.SetTag("axiom.semantic.model", provider.Model);

        SemanticProviderOutput output;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                output = await provider.AnalyzeAsync(packet, timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // R24: the deterministic result stands; coverage is reported as degraded, with a reason that carries no provider detail.
                LogProviderFailed(ex, provider.Name, provider.Model);
                return Outcome(SemanticCoverageStatus.Degraded, ex is OperationCanceledException ? "The semantic provider timed out." : "The semantic provider failed or returned an invalid answer.");
            }
        }

        AxiomTelemetry.SemanticTokens.Add(output.Usage.InputTokens + output.Usage.OutputTokens, new KeyValuePair<string, object?>("provider", provider.Name));
        var findings = Validate(output.Findings, packet, effective);
        foreach (var finding in findings)
        {
            AxiomTelemetry.SemanticFindings.Add(1,
                new KeyValuePair<string, object?>("code", finding.Code),
                new KeyValuePair<string, object?>("severity", VerdictLattice.ToContract(finding.Severity)));
        }

        activity?.SetTag("axiom.semantic.findings", findings.Length);
        return new SemanticOutcome(
            new SemanticCoverage(SemanticCoverageStatus.Complete, packet.Truncated ? "The evidence was truncated to its limits." : null, provider.Name, provider.Model),
            findings);
    }

    private ImmutableArray<Finding> Validate(ImmutableArray<RawSemanticFinding> raw, EvidencePacket packet, List<AppliedRecord> effective)
    {
        var records = effective.ToDictionary(a => a.Revision.Id, StringComparer.Ordinal);
        var changes = packet.Changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var seen = new HashSet<(string, string, int?)>();
        var accepted = new List<Finding>();

        foreach (var item in raw.OrderByDescending(r => r.Confidence).ThenBy(r => r.RecordId, StringComparer.Ordinal).ThenBy(r => r.Path, StringComparer.Ordinal))
        {
            if (item.Confidence < _options.MinConfidence
                || !packet.Records.Any(r => r.Id == item.RecordId) || !records.TryGetValue(item.RecordId, out var applied)
                || !changes.TryGetValue(item.Path, out var change))
            {
                continue;
            }

            var line = item.Line is { } n && change.AddedLines.Any(l => l.Number == n) ? n : (int?)null;
            if (!seen.Add((item.RecordId, item.Path, line)) || accepted.Count >= _options.MaxFindings)
            {
                continue;
            }

            accepted.Add(ToFinding(item, applied, line));
        }

        return [.. accepted];
    }

    /// <summary>
    /// Confidence calibration (task 7.9, first version): two bands, never BLOCK. A reviewer's overturn
    /// of a finding is recorded by <c>ReviewService</c> and feeds the thresholds over time.
    /// </summary>
    private Finding ToFinding(RawSemanticFinding item, AppliedRecord applied, int? line)
    {
        var record = applied.Record;
        var severity = item.Confidence >= _options.ReviewConfidence ? EnforcementLevel.RequireReview : EnforcementLevel.Warn;
        var owners = record.Owners.IsDefaultOrEmpty ? "the record owners" : string.Join(", ", record.Owners);
        return new Finding
        {
            Code = SemanticFindingCodes.For(item.Kind),
            Severity = severity,
            Source = FindingSource.Semantic,
            Message = Sanitize($"The change may contradict {record.Id} ({record.Title}): {item.Explanation}"),
            GovernanceIds = [record.Id],
            Evidence =
            [
                new EvidenceRef("decision", record.Id, applied.Revision.Revision.ToString(CultureInfo.InvariantCulture), applied.Revision.Provenance.Path, record.ContentHash),
                new EvidenceRef("model", $"{provider!.Name}/{provider.Model}", Locator: item.Path),
            ],
            RecommendedAction = $"Ask {owners} whether this change honors {record.Id}. This is a model suggestion: if it is wrong, overturn the finding in the review so the thresholds can be calibrated.",
            Path = item.Path,
            Line = line,
            Confidence = Math.Round(item.Confidence, 2),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic analysis by {Provider}/{Model} failed; coverage is degraded.")]
    private partial void LogProviderFailed(Exception exception, string provider, string model);

    private SemanticOutcome Outcome(SemanticCoverageStatus status, string reason) =>
        new(new SemanticCoverage(status, reason, provider?.Name, provider?.Model), []);

    /// <summary>Model text becomes part of a stored, displayed finding: bound it and strip control characters.</summary>
    private static string Sanitize(string text)
    {
        var cleaned = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);
        return cleaned.Length <= MaxMessageChars ? cleaned : cleaned[..MaxMessageChars] + "…";
    }
}
