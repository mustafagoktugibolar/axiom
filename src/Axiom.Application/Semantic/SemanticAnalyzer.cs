using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
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
    ImmutableArray<ChangedFile> Changes,
    string? OrganizationId = null,
    RepositoryTopology? Topology = null);

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
public sealed partial class SemanticAnalyzer(IOptions<SemanticOptions> options, ILogger<SemanticAnalyzer> logger, ISemanticProvider? provider = null, ISystemGraph? graph = null)
{
    private const int MaxHintSystems = 3;
    private static readonly HashSet<EntityKind> HintKinds = [EntityKind.Component, EntityKind.Api];

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
        var hints = await LoadExistingComponentsAsync(input, cancellationToken);
        var packet = EvidencePacketBuilder.Build(input.Repository, input.Task, effective, input.Changes, _options, hints);
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

    /// <summary>
    /// Task 7.6: components and APIs of the systems this repository belongs to, from the System Graph, as
    /// hints for duplicate detection. The repository's own components are excluded (the change lives in them).
    /// Best effort: a graph failure only means no hints, never a failed evaluation. Bounded by
    /// <see cref="SemanticOptions.MaxExistingComponents"/> (the packet builder applies the final cut).
    /// </summary>
    private async Task<ImmutableArray<EvidenceComponent>> LoadExistingComponentsAsync(SemanticInput input, CancellationToken cancellationToken)
    {
        if (graph is null || string.IsNullOrEmpty(input.OrganizationId) || input.Topology is null || _options.MaxExistingComponents <= 0)
        {
            return [];
        }

        var own = input.Topology.Components.ToHashSet();
        var found = new Dictionary<EntityRef, SoftwareEntity>();
        try
        {
            foreach (var system in input.Topology.Systems.Take(MaxHintSystems))
            {
                var view = await graph.GetViewAsync(input.OrganizationId, system, 2, _options.MaxExistingComponents * 2, cancellationToken);
                foreach (var node in view.Nodes.Where(n => HintKinds.Contains(n.Ref.Kind) && !own.Contains(n.Ref)))
                {
                    found.TryAdd(node.Ref, node);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHintsFailed(ex);
            return [];
        }

        return [.. found.Values.OrderBy(e => e.Ref.ToString(), StringComparer.Ordinal).Take(_options.MaxExistingComponents)
            .Select(e => new EvidenceComponent(e.Ref.ToString(), e.Title, e.Description, e.Technologies))];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Loading existing components for duplicate detection failed; continuing without them.")]
    private partial void LogHintsFailed(Exception exception);

    private ImmutableArray<Finding> Validate(ImmutableArray<RawSemanticFinding> raw, EvidencePacket packet, List<AppliedRecord> effective)
    {
        var records = effective.ToDictionary(a => a.Revision.Id, StringComparer.Ordinal);
        var changes = packet.Changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var components = packet.ExistingComponents.IsDefaultOrEmpty
            ? new Dictionary<string, EvidenceComponent>(StringComparer.Ordinal)
            : packet.ExistingComponents.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var seen = new HashSet<(SemanticFindingKind, string, string, int?)>();
        var accepted = new List<Finding>();

        foreach (var item in raw
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.Kind)
            .ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .ThenBy(r => r.EntityId, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal))
        {
            // Every finding must cite a changed file of the packet. What else is citable depends on the kind.
            if (item.Confidence < _options.MinConfidence || !changes.TryGetValue(item.Path, out var change))
            {
                continue;
            }

            AppliedRecord? applied = null;
            EvidenceComponent? existing = null;
            switch (item.Kind)
            {
                case SemanticFindingKind.DecisionConflict:
                    if (!packet.Records.Any(r => r.Id == item.RecordId) || !records.TryGetValue(item.RecordId, out var found))
                    {
                        continue;
                    }

                    applied = found;
                    break;
                case SemanticFindingKind.UnrecordedDecision:
                    // It is about the absence of a record. A record that is cited anyway must be a listed one, or the answer is distrusted.
                    if (!string.IsNullOrEmpty(item.RecordId))
                    {
                        if (!packet.Records.Any(r => r.Id == item.RecordId) || !records.TryGetValue(item.RecordId, out var cited))
                        {
                            continue;
                        }

                        applied = cited;
                    }

                    break;
                case SemanticFindingKind.PossibleDuplicateSolution:
                    if (item.EntityId is null || !components.TryGetValue(item.EntityId, out var component))
                    {
                        continue;
                    }

                    existing = component;
                    break;
                default:
                    continue;
            }

            var line = item.Line is { } n && change.AddedLines.Any(l => l.Number == n) ? n : (int?)null;
            if (!seen.Add((item.Kind, item.EntityId ?? item.RecordId, item.Path, line)) || accepted.Count >= _options.MaxFindings)
            {
                continue;
            }

            accepted.Add(item.Kind switch
            {
                SemanticFindingKind.UnrecordedDecision => ToUnrecordedFinding(item, applied, line),
                SemanticFindingKind.PossibleDuplicateSolution => ToDuplicateFinding(item, existing!, line),
                _ => ToFinding(item, applied!, line),
            });
        }

        return [.. accepted];
    }

    private EnforcementLevel Band(double confidence) => confidence >= _options.ReviewConfidence ? EnforcementLevel.RequireReview : EnforcementLevel.Warn;

    /// <summary>Task 7.7. Never BLOCK. The related record, when cited, only names the owners to ask.</summary>
    private Finding ToUnrecordedFinding(RawSemanticFinding item, AppliedRecord? related, int? line)
    {
        List<EvidenceRef> evidence = [new("model", $"{provider!.Name}/{provider.Model}", Locator: item.Path)];
        if (related is not null)
        {
            evidence.Insert(0, new EvidenceRef("decision", related.Record.Id, related.Revision.Revision.ToString(CultureInfo.InvariantCulture), related.Revision.Provenance.Path, related.Record.ContentHash));
        }

        return new Finding
        {
            Code = SemanticFindingCodes.For(item.Kind),
            Severity = Band(item.Confidence),
            Source = FindingSource.Semantic,
            Message = Sanitize($"The change may introduce an architectural decision that no applicable record covers: {item.Explanation}"),
            GovernanceIds = related is null ? [] : [related.Record.Id],
            Evidence = [.. evidence],
            RecommendedAction = "Record this choice as a decision (a short ADR in the governance repository) or point to the existing record that covers it, so the next change in this scope can be checked against it. This is a model suggestion: if it is wrong, overturn the finding in the review.",
            Path = item.Path,
            Line = line,
            Confidence = Math.Round(item.Confidence, 2),
        };
    }

    /// <summary>Task 7.6. Never BLOCK. Cites the changed file and the listed existing component.</summary>
    private Finding ToDuplicateFinding(RawSemanticFinding item, EvidenceComponent existing, int? line) => new()
    {
        Code = SemanticFindingCodes.For(item.Kind),
        Severity = Band(item.Confidence),
        Source = FindingSource.Semantic,
        Message = Sanitize($"The change may re-implement something that {existing.Id} ({existing.Title}) already provides: {item.Explanation}"),
        Evidence =
        [
            new EvidenceRef("entity", existing.Id, Locator: existing.Title),
            new EvidenceRef("model", $"{provider!.Name}/{provider.Model}", Locator: item.Path),
        ],
        RecommendedAction = $"Check whether {existing.Id} can be reused or extended instead of adding a second implementation, and ask its owners. This is a model suggestion based on catalog hints: if it is wrong, overturn the finding in the review.",
        Path = item.Path,
        Line = line,
        Confidence = Math.Round(item.Confidence, 2),
    };

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
