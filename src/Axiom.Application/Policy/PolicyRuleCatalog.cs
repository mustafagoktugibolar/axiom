using System.Collections.Immutable;
using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Rules;

namespace Axiom.Application.Policy;

/// <summary>Published documentation of one rule: what the API and CLI show to rule authors.</summary>
public sealed record PolicyRuleDescriptor(
    string RuleId,
    string Version,
    string Description,
    ImmutableSortedDictionary<string, string> Parameters);

/// <summary>Resolves rule IDs used in <c>enforcement.rules[].ruleId</c> to trusted rule implementations.</summary>
public interface IPolicyRuleCatalog
{
    /// <summary>The rule with this ID, or null when no such rule is registered.</summary>
    IPolicyRule? Find(string ruleId);

    /// <summary>Every registered rule, ordered by rule ID.</summary>
    ImmutableArray<PolicyRuleDescriptor> Describe();
}

/// <summary>An immutable rule registry. Rule IDs are unique and matched exactly.</summary>
public sealed class PolicyRuleCatalog : IPolicyRuleCatalog
{
    private readonly ImmutableSortedDictionary<string, IPolicyRule> _rules;

    public PolicyRuleCatalog(IEnumerable<IPolicyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, IPolicyRule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (string.IsNullOrWhiteSpace(rule.RuleId))
            {
                throw new ArgumentException("A policy rule must have a rule ID.", nameof(rules));
            }

            if (!builder.TryAdd(rule.RuleId, rule))
            {
                throw new ArgumentException($"Policy rule '{rule.RuleId}' is registered more than once.", nameof(rules));
            }
        }

        _rules = builder.ToImmutable();
    }

    /// <summary>The catalog of trusted built-in rules.</summary>
    public static PolicyRuleCatalog BuiltIn { get; } = new(BuiltInRules.All);

    public IPolicyRule? Find(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return _rules.GetValueOrDefault(ruleId);
    }

    public ImmutableArray<PolicyRuleDescriptor> Describe() =>
    [
        .. _rules.Values.Select(rule => new PolicyRuleDescriptor(
            rule.RuleId,
            rule.Version,
            rule.Description,
            rule.Parameters.ToImmutableSortedDictionary(StringComparer.Ordinal))),
    ];
}
