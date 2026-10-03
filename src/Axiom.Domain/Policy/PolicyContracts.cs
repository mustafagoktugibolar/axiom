using System.Collections.Immutable;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;

namespace Axiom.Domain.Policy;

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>A line added by the change, with its 1-based line number in the new file.</summary>
public sealed record AddedLine(int Number, string Text);

/// <summary>One file touched by the change. Content is null for deleted or binary files.</summary>
public sealed record ChangedFile(
    string Path,
    ChangeKind Kind,
    string? PreviousPath,
    string? Content,
    string? PreviousContent,
    ImmutableArray<AddedLine> AddedLines);

/// <summary>
/// Read-only view of the repository tree at the evaluated commit. Rules never see credentials and
/// can never write (structure.md: policy implementations never mutate governance state).
/// </summary>
public interface IRepositoryView
{
    bool Exists(string path);

    string? Read(string path);

    /// <summary>All file paths in the tree, in ordinal order.</summary>
    IEnumerable<string> Paths { get; }
}

/// <summary>Everything a deterministic rule may look at.</summary>
public sealed record PolicyContext(
    string Repository,
    ImmutableArray<ChangedFile> Changes,
    IRepositoryView Tree,
    EvaluationScope Scope);

public sealed record PolicyViolation(string Message, string? Path = null, int? Line = null, string? Evidence = null);

public enum PolicyOutcome
{
    Passed,
    Violated,

    /// <summary>The rule could not be evaluated (bad parameters, crash, timeout). Never treated as a pass.</summary>
    Error,
}

/// <summary>
/// Result contract of one rule execution (task 5.1). It contains no timing or environment data, so
/// identical inputs and rule versions give an identical result (P3).
/// </summary>
public sealed record PolicyRunResult(
    string RuleId,
    string RuleVersion,
    string RuleHash,
    string RecordId,
    EnforcementLevel Mode,
    PolicyOutcome Outcome,
    ImmutableArray<PolicyViolation> Violations,
    string? Error = null);

/// <summary>
/// A trusted, built-in deterministic rule. Implementations must be pure functions of the context and
/// parameters: no clock, randomness, network, or mutable static state.
/// </summary>
public interface IPolicyRule
{
    /// <summary>Stable kebab-case identifier referenced from <c>enforcement.rules[].ruleId</c>.</summary>
    string RuleId { get; }

    /// <summary>Semantic version; bump whenever behaviour changes so receipts stay reproducible.</summary>
    string Version { get; }

    string Description { get; }

    /// <summary>Parameter names accepted in <c>with</c>, each with a description. Unknown parameters are an error.</summary>
    IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Returns the violations found. Throwing <see cref="PolicyParameterException"/> reports a configuration error.</summary>
    IEnumerable<PolicyViolation> Evaluate(PolicyContext context, IReadOnlyDictionary<string, string> parameters);
}

/// <summary>Thrown by a rule when its parameters are missing or malformed.</summary>
public sealed class PolicyParameterException : Exception
{
    public PolicyParameterException()
    {
    }

    public PolicyParameterException(string message)
        : base(message)
    {
    }

    public PolicyParameterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
