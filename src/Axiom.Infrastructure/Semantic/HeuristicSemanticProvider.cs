using System.Collections.Immutable;
using System.Globalization;
using Axiom.Application.Semantic;

namespace Axiom.Infrastructure.Semantic;

/// <summary>
/// A local provider with no model: it reports an added line when it shares most of its distinctive
/// words with something a decision explicitly forbids. It exists so semantic analysis can be tried,
/// tested and run in scopes that forbid external LLMs; it is a lexical heuristic, not understanding,
/// and its confidence is capped accordingly.
/// </summary>
public sealed class HeuristicSemanticProvider : ISemanticProvider
{
    private const int MinSharedWords = 3;
    private const double MinRatio = 0.5;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "that", "this", "with", "from", "into", "than", "then", "when", "where", "which", "their", "there", "should", "must", "have",
        "been", "does", "only", "also", "other", "each", "every", "such", "using", "used", "make", "made", "over", "under", "without",
    };

    private static readonly char[] Separators =
        [' ', '\t', '.', ',', ';', ':', '(', ')', '"', '\'', '/', '\\', '-', '_', '<', '>', '[', ']', '{', '}', '=', '!', '?', '*', '+', '|', '&'];

    public string Name => "heuristic";

    public string Model => "lexical-overlap-1";

    public bool IsExternal => false;

    public Task<SemanticProviderOutput> AnalyzeAsync(EvidencePacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var findings = ImmutableArray.CreateBuilder<RawSemanticFinding>();

        foreach (var record in packet.Records)
        {
            foreach (var forbidden in record.Forbidden)
            {
                var words = Words(forbidden);
                if (words.Count < MinSharedWords)
                {
                    continue;
                }

                foreach (var change in packet.Changes)
                {
                    var best = change.AddedLines
                        .Select(l => (Line: l, Shared: Words(l.Text).Intersect(words).Count()))
                        .Where(x => x.Shared >= MinSharedWords && (double)x.Shared / words.Count >= MinRatio)
                        .OrderByDescending(x => x.Shared)
                        .ThenBy(x => x.Line.Number)
                        .FirstOrDefault();
                    if (best.Line is null)
                    {
                        continue;
                    }

                    var ratio = (double)best.Shared / words.Count;
                    findings.Add(new RawSemanticFinding(
                        SemanticFindingKind.DecisionConflict,
                        record.Id,
                        change.Path,
                        best.Line.Number,
                        string.Create(CultureInfo.InvariantCulture, $"The added line shares {best.Shared} of {words.Count} key words with something this record forbids (\"{forbidden}\"). Lexical match only; a person should confirm."),
                        Math.Round(0.5 + (0.4 * ratio), 2)));
                }
            }
        }

        return Task.FromResult(new SemanticProviderOutput(findings.ToImmutable(), new SemanticUsage(0, 0)));
    }

    private static HashSet<string> Words(string text) =>
    [
        .. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant().TrimEnd('s'))
            .Where(w => w.Length >= 4 && !Stop.Contains(w)),
    ];
}
