using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Semantic;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Axiom.Application.Candidates;

/// <summary>
/// Task 7.8: turns SEMANTIC_UNRECORDED_DECISION findings of a stored evaluation into proposed candidates.
/// Opt-in (<c>Axiom:Semantic:ExtractCandidates</c>), run after the evaluation is stored, and fail-safe: nothing
/// here can change the verdict or the response, so every failure is logged and swallowed. It reads only what the
/// evidence packet already carried (the finding and the added lines of the diff, scrubbed and clipped the same way).
/// Model text is untrusted: the title and statement are bounded and sanitized with the analyzer's rules.
/// </summary>
public sealed partial class SemanticCandidateExtractor(IOptions<SemanticOptions> options, CandidateService candidates, ILogger<SemanticCandidateExtractor> logger)
{
    public const string ExtractionMethod = "semantic/unrecorded-decision@1";
    public const int MaxTitleChars = 120;
    public const int MaxStatementChars = 600;

    private readonly SemanticOptions _options = options.Value;

    public async Task<CandidateIngestResult?> ExtractAsync(
        AxiomPrincipal principal, EvaluationRecord evaluation, string repository, RepositoryTopology? topology, IEnumerable<ChangedFile> changes, CancellationToken cancellationToken)
    {
        if (!_options.ExtractCandidates)
        {
            return null;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentNullException.ThrowIfNull(evaluation);
            var inputs = Derive(evaluation, repository, topology, changes);
            return inputs.IsEmpty ? null : await candidates.IngestExtractedAsync(principal, inputs, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(ex, evaluation?.Id);
            return null;
        }
    }

    /// <summary>Pure derivation, exposed for testing. Deterministic and bounded by the number of findings.</summary>
    public ImmutableArray<CandidateInput> Derive(EvaluationRecord evaluation, string repository, RepositoryTopology? topology, IEnumerable<ChangedFile> changes)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(changes);
        var files = changes.GroupBy(c => c.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var owner = topology is null || topology.Owners.IsDefaultOrEmpty
            ? null
            : topology.Owners.Select(o => o.Name).Order(StringComparer.Ordinal).First();

        var result = ImmutableArray.CreateBuilder<CandidateInput>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in evaluation.Findings
            .Where(f => f.Code == SemanticFindingCodes.UnrecordedDecision && f.Source == FindingSource.Semantic && !f.IsWaived
                && f.Confidence is { } c && double.IsFinite(c) && c >= _options.ExtractionMinConfidence && !string.IsNullOrWhiteSpace(f.Path))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Line ?? 0))
        {
            var explanation = ExplanationOf(finding.Message);
            if (explanation.Length < 3)
            {
                continue;
            }

            var excerpt = ExcerptOf(finding, files);
            var hash = Hash(repository, finding.Path!, excerpt ?? explanation);
            if (!hashes.Add(hash))
            {
                continue;
            }

            var location = finding.Line is { } line ? $"{finding.Path}:{line.ToString(CultureInfo.InvariantCulture)}" : finding.Path!;
            var title = Clip(Squash($"Unrecorded decision in {finding.Path}: {explanation}"), MaxTitleChars);
            result.Add(new CandidateInput(
                $"evaluation:{evaluation.Id}#{location}",
                hash,
                ExtractionMethod,
                finding.Confidence!.Value,
                owner,
                RecordKind.Decision,
                title,
                Clip(explanation, MaxStatementChars),
                BuildRationale(evaluation.Id, location, excerpt),
                new Dictionary<string, string[]> { ["repositories"] = [repository] }));
        }

        return result.ToImmutable();
    }

    private static string ExplanationOf(string message)
    {
        var text = message.StartsWith(SemanticAnalyzer.UnrecordedMessagePrefix, StringComparison.Ordinal) ? message[SemanticAnalyzer.UnrecordedMessagePrefix.Length..] : message;
        return Clip(Squash(EvidencePacketBuilder.Scrub(SemanticAnalyzer.Sanitize(text))), MaxStatementChars);
    }

    /// <summary>The added line the finding points at, scrubbed and clipped like the packet's lines; null when it is not citable.</summary>
    private string? ExcerptOf(Finding finding, Dictionary<string, ChangedFile> files)
    {
        if (finding.Line is not { } line || !files.TryGetValue(finding.Path!, out var file) || file.AddedLines.IsDefaultOrEmpty)
        {
            return null;
        }

        var text = file.AddedLines.FirstOrDefault(l => l.Number == line)?.Text;
        return string.IsNullOrWhiteSpace(text) ? null : EvidencePacketBuilder.Scrub(Clip(text.Trim(), _options.MaxLineChars));
    }

    private static string BuildRationale(string evaluationId, string location, string? excerpt)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Suggested by the semantic analyzer while evaluating a change (evaluation {evaluationId}, {location}). It is a model suggestion and unverified: a human owner decides whether this is a decision worth recording.");
        if (excerpt is not null)
        {
            text.AppendLine().AppendLine("Excerpt of the added line:").AppendLine().Append("    ").Append(Squash(excerpt));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Identity of the knowledge: where and what, never which evaluation, so re-evaluating the same change is a no-op.</summary>
    internal static string Hash(string repository, string path, string excerpt)
    {
        var normalized = Squash(excerpt).ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"axiom-semantic-excerpt-v1\n{repository}\n{path}\n{normalized}")));
    }

    private static string Squash(string text) => string.Join(' ', new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extracting candidates from evaluation {EvaluationId} failed; the evaluation is unaffected.")]
    private partial void LogFailed(Exception exception, string? evaluationId);
}
