using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Axiom.Application.Exceptions;
using Axiom.Domain.Governance;

namespace Axiom.Application.Candidates;

/// <summary>
/// Renders the governance record a candidate would become. Axiom never writes to the Git repository
/// (ADR-0001): a human commits the draft, and only the merged, accepted record governs.
/// </summary>
public static class CandidateRecordDraft
{
    public const string UnassignedOwner = "unassigned";

    /// <summary>Candidates may become decisions, standards, principles or goals; exceptions have their own workflow.</summary>
    public static bool IsSupportedKind(RecordKind kind) => kind is RecordKind.Decision or RecordKind.Standard or RecordKind.Principle or RecordKind.Goal;

    public static string DirectoryFor(RecordKind kind) => kind switch
    {
        RecordKind.Decision => "decisions",
        RecordKind.Standard => "standards",
        RecordKind.Principle => "principles",
        RecordKind.Goal => "goals",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a candidate kind."),
    };

    /// <summary>A placeholder ID matching the record ID pattern (<c>PREFIX-123</c>); the maintainer renames it when committing.</summary>
    public static string DraftId(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var hex = candidate.Id.AsSpan(4, 8);
        return "CAND-" + (uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public static string PathFor(Candidate candidate) => $"governance/{DirectoryFor(candidate.Draft.Kind)}/{DraftId(candidate)}.md";

    public static ImmutableArray<string> OwnersOf(Candidate candidate) =>
        string.IsNullOrWhiteSpace(candidate.Evidence.SuggestedOwner) ? [UnassignedOwner] : [candidate.Evidence.SuggestedOwner];

    /// <summary>Markdown with YAML front matter; always <c>status: proposed</c>, whatever the candidate's state.</summary>
    public static string Render(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var draft = candidate.Draft;
        var yaml = new StringBuilder();
        yaml.AppendLine("---");
        yaml.AppendLine("schemaVersion: axiom.io/v1");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"kind: {draft.Kind}");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  id: {DraftId(candidate)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  title: {Quote(draft.Title)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  owners: {Flow(OwnersOf(candidate))}");
        yaml.AppendLine("  tags: [\"candidate\"]");
        yaml.AppendLine("spec:");
        yaml.AppendLine("  status: proposed");
        if (draft.ScopeHints.IsEmpty)
        {
            yaml.AppendLine("  scope: {}");
        }
        else
        {
            yaml.AppendLine("  scope:");
            foreach (var (dimension, values) in draft.ScopeHints)
            {
                yaml.AppendLine(CultureInfo.InvariantCulture, $"    {dimension}: {Flow(values)}");
            }
        }

        yaml.AppendLine("  authority:");
        yaml.AppendLine("    level: advisory");
        yaml.AppendLine("    exemptable: true");
        yaml.AppendLine("  enforcement:");
        yaml.AppendLine("    defaultVerdict: info");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  {(draft.Kind == RecordKind.Decision ? "decision" : "statement")}: {Quote(draft.Statement)}");
        yaml.AppendLine("  evidence:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"    - source: {Quote(candidate.Evidence.SourceLocator)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"      excerptHash: {Quote(candidate.Evidence.ExcerptHash)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"      method: {Quote(candidate.Evidence.ExtractionMethod)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"      confidence: {candidate.Evidence.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(candidate.Evidence.SuggestedOwner))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"      suggestedOwner: {Quote(candidate.Evidence.SuggestedOwner)}");
        }

        yaml.AppendLine("---");
        if (!string.IsNullOrWhiteSpace(draft.Rationale))
        {
            yaml.AppendLine();
            yaml.AppendLine(draft.Rationale.Trim());
        }

        return yaml.ToString();
    }

    /// <summary>The draft as a domain record, so the rules that guard the Git repository can vet it. Always <see cref="LifecycleStatus.Proposed"/>.</summary>
    public static GovernanceRecord ToRecord(Candidate candidate) => new()
    {
        Id = DraftId(candidate),
        Kind = candidate.Draft.Kind,
        SchemaVersion = "axiom.io/v1",
        Title = candidate.Draft.Title,
        Owners = OwnersOf(candidate),
        Tags = ["candidate"],
        Status = LifecycleStatus.Proposed,
        Scope = Scope.Create(candidate.Draft.ScopeHints.Select(kv => new KeyValuePair<ScopeDimension, IEnumerable<string>>(ExceptionScopeKeys.Dimension(kv.Key)!.Value, kv.Value))),
        Authority = new Authority(AuthorityLevel.Advisory, true),
        Enforcement = new Enforcement(EnforcementLevel.Info, []),
        Statement = candidate.Draft.Statement,
        Evidence = [candidate.Evidence],
        Body = candidate.Draft.Rationale,
        ContentHash = "draft",
    };

    private static string Flow(ImmutableArray<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";

    // JSON string syntax is valid YAML double-quoted syntax, so no content can break out of its field.
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
