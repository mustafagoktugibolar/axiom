using System.Collections.Immutable;
using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>
/// Base of the trusted built-in rules. It validates parameter names before any evaluation and
/// materializes the result, so configuration errors surface immediately and a rule can never be
/// left half-enumerated. Derived rules are pure: no clock, randomness, I/O or mutable static state.
/// </summary>
public abstract class BuiltInRule : IPolicyRule
{
    public abstract string RuleId { get; }

    public abstract string Version { get; }

    public abstract string Description { get; }

    public abstract IReadOnlyDictionary<string, string> Parameters { get; }

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bound = RuleParameters.Bind(this, parameters);
        return Run(context, bound).ToImmutableArray();
    }

    private protected abstract IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters);

    private protected static ImmutableSortedDictionary<string, string> Describe(params (string Name, string Description)[] parameters) =>
        parameters.ToImmutableSortedDictionary(p => p.Name, p => p.Description, StringComparer.Ordinal);

    private protected static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Modified => "modified",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
