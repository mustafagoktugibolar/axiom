using System.Collections.Immutable;
using Axiom.Domain.Policy;

namespace Axiom.Application.Policy.Custom;

/// <summary>
/// The built-in catalog plus custom rules. Custom rules live only under the <c>custom/</c> namespace, so
/// they cannot replace or shadow a built-in rule, and built-in rules are never looked up in the custom set.
/// </summary>
public sealed class CompositePolicyRuleCatalog : IPolicyRuleCatalog
{
    private readonly IPolicyRuleCatalog _builtIn;
    private readonly PolicyRuleCatalog _custom;

    public CompositePolicyRuleCatalog(IPolicyRuleCatalog builtIn, IEnumerable<IPolicyRule> customRules)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(customRules);
        var rules = customRules.ToArray();
        foreach (var rule in rules.Where(r => !r.RuleId.StartsWith(CustomRuleDefinition.Namespace, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Custom rule '{rule.RuleId}' must be in the '{CustomRuleDefinition.Namespace}' namespace.", nameof(customRules));
        }

        _builtIn = builtIn;
        _custom = new PolicyRuleCatalog(rules);
    }

    public IPolicyRule? Find(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return ruleId.StartsWith(CustomRuleDefinition.Namespace, StringComparison.Ordinal) ? _custom.Find(ruleId) : _builtIn.Find(ruleId);
    }

    public ImmutableArray<PolicyRuleDescriptor> Describe() =>
        [.. _builtIn.Describe().Concat(_custom.Describe()).OrderBy(d => d.RuleId, StringComparer.Ordinal)];
}
