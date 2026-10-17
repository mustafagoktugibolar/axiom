using System.Collections.Immutable;

namespace Axiom.Application.Semantic;

/// <summary>What the semantic analyzer found. Closed set so a provider cannot invent finding types.</summary>
public enum SemanticFindingKind
{
    /// <summary>The change appears to contradict the intent of an applicable decision, in a way no lint rule expresses.</summary>
    DecisionConflict,
}

/// <summary>Stable codes of semantic findings (R9: semantic findings are advisory by default, ADR-0005).</summary>
public static class SemanticFindingCodes
{
    public const string PotentialDecisionConflict = "SEMANTIC_POTENTIAL_DECISION_CONFLICT";

    public static string For(SemanticFindingKind kind) => kind switch
    {
        SemanticFindingKind.DecisionConflict => PotentialDecisionConflict,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>A governance record included in the packet. Text is already bounded.</summary>
public sealed record EvidenceRecord(
    string Id,
    string Title,
    string Kind,
    string ContentHash,
    string Statement,
    ImmutableArray<string> Forbidden,
    ImmutableArray<string> Preferred);

/// <summary>The added lines of one changed file, bounded and redacted.</summary>
public sealed record EvidenceChange(string Path, ImmutableArray<EvidenceLine> AddedLines);

public sealed record EvidenceLine(int Number, string Text);

/// <summary>
/// Everything a provider may see (design.md §4.6): a bounded, redacted evidence packet, never the
/// organization corpus or the whole repository. <see cref="Truncated"/> says the bounds cut content.
/// </summary>
public sealed record EvidencePacket(
    string Repository,
    string? Task,
    ImmutableArray<EvidenceRecord> Records,
    ImmutableArray<EvidenceChange> Changes,
    bool Truncated)
{
    public bool HasContent => !Records.IsDefaultOrEmpty && (!Changes.IsDefaultOrEmpty || !string.IsNullOrWhiteSpace(Task));
}

/// <summary>A finding exactly as a provider returned it, before validation against the packet.</summary>
public sealed record RawSemanticFinding(
    SemanticFindingKind Kind,
    string RecordId,
    string Path,
    int? Line,
    string Explanation,
    double Confidence);

public sealed record SemanticUsage(int InputTokens, int OutputTokens);

public sealed record SemanticProviderOutput(ImmutableArray<RawSemanticFinding> Findings, SemanticUsage Usage);

/// <summary>
/// Provider abstraction (task 7.1). A provider turns an evidence packet into typed findings; how it
/// does that (hosted model, self-hosted model, local heuristics) is its own business. It must treat
/// the packet as untrusted data and must throw rather than return partial or unparseable output.
/// </summary>
public interface ISemanticProvider
{
    string Name { get; }

    string Model { get; }

    /// <summary>True when packet content leaves the deployment. Scopes that forbid external LLMs exclude these (task 7.12).</summary>
    bool IsExternal { get; }

    Task<SemanticProviderOutput> AnalyzeAsync(EvidencePacket packet, CancellationToken cancellationToken);
}

/// <summary>Semantic analysis settings, bound from <c>Axiom:Semantic</c>. Off by default.</summary>
public sealed class SemanticOptions
{
    public const string SectionName = "Axiom:Semantic";

    public bool Enabled { get; set; }

    /// <summary><c>Heuristic</c> (local, no model, no data leaves the deployment) or <c>Anthropic</c>.</summary>
    public string Provider { get; set; } = "Heuristic";

    /// <summary>Findings below this confidence are dropped.</summary>
    public double MinConfidence { get; set; } = 0.5;

    /// <summary>Findings at or above this confidence ask for review; those between the two only warn.</summary>
    public double ReviewConfidence { get; set; } = 0.8;

    public int MaxRecords { get; set; } = 12;

    public int MaxRecordChars { get; set; } = 1_500;

    public int MaxFiles { get; set; } = 25;

    public int MaxLinesPerFile { get; set; } = 120;

    public int MaxLineChars { get; set; } = 240;

    public int MaxTaskChars { get; set; } = 1_000;

    public int MaxFindings { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 20;
}
