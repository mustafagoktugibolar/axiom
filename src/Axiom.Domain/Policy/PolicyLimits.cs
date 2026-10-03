namespace Axiom.Domain.Policy;

/// <summary>
/// Hard bounds on the work a deterministic rule may do. Inputs are untrusted repository content, so
/// every limit fails closed: content beyond a limit is reported, never silently treated as compliant.
/// </summary>
public static class PolicyLimits
{
    /// <summary>Files whose content is longer than this many characters are not parsed by any rule.</summary>
    public const int MaxFileCharacters = 1_000_000;

    /// <summary>Lines longer than this are not pattern-scanned; the line is reported instead.</summary>
    public const int MaxLineCharacters = 20_000;

    /// <summary>Maximum nesting depth accepted by the structured-document readers and contract comparison.</summary>
    public const int MaxNestingDepth = 64;

    /// <summary>Maximum number of violations kept from one rule execution; the remainder is summarized.</summary>
    public const int MaxViolationsPerExecution = 500;

    public static bool IsOversized(string? content) => content is not null && content.Length > MaxFileCharacters;
}
