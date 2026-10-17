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

        findings.AddRange(UnrecordedDecisions(packet));
        return Task.FromResult(new SemanticProviderOutput(findings.ToImmutable(), new SemanticUsage(0, 0)));
    }

    /// <summary>
    /// Task 7.7, deliberately narrow: a dependency manifest line that adds a well-known datastore, messaging
    /// or RPC client library, when no packet record mentions that technology. Source code, other manifests and
    /// anything not in the table are not looked at, so it will miss most real cases. It never reports a
    /// possible duplicate solution (task 7.6): telling that two components do the same thing needs semantics
    /// a lexical match cannot give, so that kind is only produced by a model-backed provider.
    /// </summary>
    private static IEnumerable<RawSemanticFinding> UnrecordedDecisions(EvidencePacket packet)
    {
        var recordText = string.Join('\n', packet.Records.Select(r => $"{r.Title} {r.Statement} {string.Join(' ', r.Forbidden)} {string.Join(' ', r.Preferred)}")).ToLowerInvariant();
        foreach (var change in packet.Changes.Where(c => IsManifest(c.Path)))
        {
            foreach (var (category, names, tokens) in Technologies)
            {
                if (names.Any(recordText.Contains))
                {
                    continue;
                }

                var line = change.AddedLines.FirstOrDefault(l => tokens.Any(t => l.Text.Contains(t, StringComparison.OrdinalIgnoreCase)));
                if (line is null)
                {
                    continue;
                }

                yield return new RawSemanticFinding(
                    SemanticFindingKind.UnrecordedDecision,
                    string.Empty,
                    change.Path,
                    line.Number,
                    $"The dependency manifest adds a {category} client ({names[0]}), and no applicable record mentions it. Lexical match only; a person should confirm.",
                    0.6);
            }
        }
    }

    private static bool IsManifest(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) || name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase);
    }

    // category, words a record would use to cover it (lowercase), dependency tokens that signal the choice.
    private static readonly (string Category, string[] Names, string[] Tokens)[] Technologies =
    [
        ("datastore", ["mongodb", "mongo"], ["MongoDB.Driver", "\"mongodb\"", "mongoose", "pymongo"]),
        ("datastore", ["redis"], ["StackExchange.Redis", "\"redis\"", "\"ioredis\""]),
        ("datastore", ["elasticsearch", "opensearch"], ["Elastic.Clients", "\"@elastic/elasticsearch\"", "OpenSearch.Client"]),
        ("datastore", ["cassandra"], ["CassandraCSharpDriver", "cassandra-driver"]),
        ("datastore", ["dynamodb"], ["AWSSDK.DynamoDBv2"]),
        ("messaging", ["kafka"], ["Confluent.Kafka", "\"kafkajs\"", "kafka-clients"]),
        ("messaging", ["rabbitmq"], ["RabbitMQ.Client", "\"amqplib\""]),
        ("messaging", ["service bus", "servicebus"], ["Azure.Messaging.ServiceBus"]),
        ("messaging", ["nats"], ["NATS.Client", "\"nats\""]),
        ("messaging", ["masstransit"], ["MassTransit"]),
        ("cross-service protocol", ["grpc"], ["Grpc.", "\"@grpc/grpc-js\"", "io.grpc"]),
        ("cross-service protocol", ["graphql"], ["HotChocolate", "GraphQL.", "\"graphql\"", "\"apollo-server"]),
    ];

    private static HashSet<string> Words(string text) =>
    [
        .. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant().TrimEnd('s'))
            .Where(w => w.Length >= 4 && !Stop.Contains(w)),
    ];
}
