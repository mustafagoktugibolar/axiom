using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Resolution;

namespace Axiom.Application.Policy;

/// <summary>Stable machine-readable codes of the findings the policy engine produces.</summary>
public static class PolicyFindingCodes
{
    /// <summary>A rule found a violation of the record it is bound to.</summary>
    public const string Violation = "POLICY_VIOLATION";

    /// <summary>A rule failed while evaluating. Never treated as a pass (P9).</summary>
    public const string RuleError = "POLICY_RULE_ERROR";

    /// <summary>A binding's parameters are missing, unknown or malformed.</summary>
    public const string RuleMisconfigured = "POLICY_RULE_MISCONFIGURED";

    /// <summary>A record binds a rule ID that is not registered.</summary>
    public const string RuleUnknown = "POLICY_RULE_UNKNOWN";

    /// <summary>A changed file exceeds <see cref="PolicyLimits.MaxFileCharacters"/> and its content was not evaluated.</summary>
    public const string FileTooLarge = "POLICY_FILE_TOO_LARGE";
}

/// <summary>A rule binding that was deliberately not executed, and why.</summary>
public sealed record SkippedRule(string RecordId, string RuleId, string Reason);

/// <summary>
/// Outcome of running every applicable rule binding. It carries no timestamps or durations and is
/// fully ordered, so identical inputs and rule versions produce an identical value (P3).
/// </summary>
public sealed record PolicyEvaluation(
    ImmutableArray<PolicyRunResult> Results,
    ImmutableArray<Finding> Findings,
    ImmutableArray<SkippedRule> Skipped);

/// <summary>
/// The deterministic policy engine (design.md §4.5, ADR-0002). It executes the rule bindings of the
/// records that apply to a change and maps the results to findings. It only reads: it holds no
/// reference to governance stores and cannot change records or grant exceptions.
/// </summary>
public sealed class PolicyEngine
{
    private readonly IPolicyRuleCatalog _catalog;

    public PolicyEngine(IPolicyRuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>
    /// SHA-256 over the rule ID, the rule version and the canonical (ordinally sorted, length-prefixed)
    /// parameter set. Recorded with every execution so a receipt identifies exactly what ran (task 5.9).
    /// </summary>
    public static string ComputeRuleHash(string ruleId, string ruleVersion, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(parameters);

        var canonical = new StringBuilder("axiom-policy-rule-v1\n");
        Append(canonical, ruleId);
        Append(canonical, ruleVersion);
        foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(canonical, name);
            Append(canonical, value ?? string.Empty);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        static void Append(StringBuilder builder, string value) =>
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
    }

    public PolicyEvaluation Evaluate(PolicyContext context, ResolutionResult resolution)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolution);

        var normalized = Normalize(context, out var oversized);
        var results = new List<PolicyRunResult>();
        var findings = new List<Finding>();
        var skipped = new List<SkippedRule>();

        var records = resolution.Applicable
            .GroupBy(a => a.Revision.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.First());

        foreach (var applied in records)
        {
            var bindings = (applied.Record.Enforcement.Rules.IsDefault ? [] : applied.Record.Enforcement.Rules)
                .Select(b => (Binding: b, Rule: _catalog.Find(b.RuleId)))
                .Select(b => (b.Binding, b.Rule, Hash: ComputeRuleHash(b.Binding.RuleId, b.Rule?.Version ?? UnknownVersion, b.Binding.Parameters)))
                .DistinctBy(b => (b.Binding.RuleId, b.Hash, b.Binding.Mode))
                .OrderBy(b => b.Binding.RuleId, StringComparer.Ordinal)
                .ThenBy(b => b.Hash, StringComparer.Ordinal)
                .ThenBy(b => b.Binding.Mode);

            var supersededBy = SupersedingReason(applied);
            foreach (var (binding, rule, hash) in bindings)
            {
                if (supersededBy is not null)
                {
                    skipped.Add(new SkippedRule(applied.Revision.Id, binding.RuleId, supersededBy));
                    continue;
                }

                var result = Execute(normalized, applied, binding, rule, hash);
                results.Add(result);
                findings.AddRange(ToFindings(result, applied, resolution, normalized.Scope));
            }
        }

        if (results.Count > 0)
        {
            findings.AddRange(oversized.Select(path => new Finding
            {
                Code = PolicyFindingCodes.FileTooLarge,
                Severity = EnforcementLevel.RequireReview,
                Source = FindingSource.Policy,
                Path = path,
                Message = $"'{path}' exceeds {PolicyLimits.MaxFileCharacters} characters; its content was not evaluated by any policy rule.",
                RecommendedAction = "Split or shrink the file, or have a reviewer confirm it complies with the applicable records.",
                Evidence = [new EvidenceRef("changed-file", Locator: path)],
            }));
        }

        return new PolicyEvaluation([.. results], [.. findings], [.. skipped]);
    }

    private const string UnknownVersion = "unknown";

    private static string? SupersedingReason(AppliedRecord applied)
    {
        if (!applied.OverriddenBy.IsDefaultOrEmpty)
        {
            return $"overridden by {string.Join(", ", applied.OverriddenBy.Order(StringComparer.Ordinal))}";
        }

        return applied.RefinedBy.IsDefaultOrEmpty
            ? null
            : $"refined by {string.Join(", ", applied.RefinedBy.Order(StringComparer.Ordinal))}";
    }

    private static PolicyRunResult Execute(PolicyContext context, AppliedRecord applied, RuleBinding binding, IPolicyRule? rule, string hash)
    {
        PolicyRunResult Result(PolicyOutcome outcome, ImmutableArray<PolicyViolation> violations, string? error = null) =>
            new(binding.RuleId, rule?.Version ?? UnknownVersion, hash, applied.Revision.Id, binding.Mode, outcome, violations, error);

        if (rule is null)
        {
            return Result(PolicyOutcome.Error, [], $"Rule '{binding.RuleId}' is not registered.");
        }

        try
        {
            var raw = rule.Evaluate(context, binding.Parameters);
            if (raw is null)
            {
                return Result(PolicyOutcome.Error, [], $"Rule '{binding.RuleId}' returned no result.");
            }

            var violations = raw
                .Where(v => v is not null)
                .Distinct()
                .OrderBy(v => v.Path, StringComparer.Ordinal)
                .ThenBy(v => v.Line)
                .ThenBy(v => v.Message, StringComparer.Ordinal)
                .ThenBy(v => v.Evidence, StringComparer.Ordinal)
                .ToList();

            if (violations.Count > PolicyLimits.MaxViolationsPerExecution)
            {
                var omitted = violations.Count - PolicyLimits.MaxViolationsPerExecution;
                violations.RemoveRange(PolicyLimits.MaxViolationsPerExecution, omitted);
                violations.Add(new PolicyViolation($"{omitted} further violation(s) of this rule were omitted."));
            }

            return Result(violations.Count == 0 ? PolicyOutcome.Passed : PolicyOutcome.Violated, [.. violations]);
        }
        catch (PolicyParameterException ex)
        {
            return Result(PolicyOutcome.Error, [], ex.Message);
        }
#pragma warning disable CA1031 // A failing rule must be isolated and reported as an error; it must not stop the other rules.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Only the exception type is recorded: its message could quote repository content.
            return Result(PolicyOutcome.Error, [], $"Rule '{binding.RuleId}' failed with {ex.GetType().Name}.");
        }
    }

    private IEnumerable<Finding> ToFindings(PolicyRunResult result, AppliedRecord applied, ResolutionResult resolution, EvaluationScope scope)
    {
        var record = applied.Record;
        var recordEvidence = new EvidenceRef(
            "governance-record", record.Id, applied.Revision.Revision.ToString(CultureInfo.InvariantCulture), Hash: record.ContentHash);
        var owners = record.Owners.IsDefaultOrEmpty ? "its owners" : string.Join(", ", record.Owners);

        if (result.Outcome == PolicyOutcome.Error)
        {
            var rule = _catalog.Find(result.RuleId);
            var misconfigured = rule is not null && result.Error is not null && !result.Error.Contains(" failed with ", StringComparison.Ordinal)
                && !result.Error.EndsWith("returned no result.", StringComparison.Ordinal);

            // P9: a rule that could not be evaluated is never a pass and is never waived.
            yield return new Finding
            {
                Code = rule is null ? PolicyFindingCodes.RuleUnknown : misconfigured ? PolicyFindingCodes.RuleMisconfigured : PolicyFindingCodes.RuleError,
                Severity = (EnforcementLevel)Math.Max((int)result.Mode, (int)EnforcementLevel.RequireReview),
                Source = FindingSource.Policy,
                Message = $"{record.Id}: rule '{result.RuleId}' could not be evaluated. {result.Error}",
                GovernanceIds = [record.Id],
                RuleId = result.RuleId,
                RuleVersion = result.RuleVersion,
                Evidence = [recordEvidence, new EvidenceRef("policy-rule", result.RuleId, result.RuleVersion, Hash: result.RuleHash)],
                RecommendedAction = rule is null || misconfigured
                    ? $"Have {owners} correct the enforcement binding of {record.Id}; a reviewer must confirm compliance until then."
                    : "Have a reviewer confirm compliance with the record, and report the rule failure to the Axiom operators.",
            };
            yield break;
        }

        foreach (var violation in result.Violations)
        {
            var path = string.IsNullOrWhiteSpace(violation.Path) ? null : violation.Path;
            var locator = path is null ? null : violation.Line is null ? path : $"{path}:{violation.Line.Value.ToString(CultureInfo.InvariantCulture)}";
            var waivedBy = resolution.WaiverFor(record.Id, scope, path);

            yield return new Finding
            {
                Code = PolicyFindingCodes.Violation,
                Severity = result.Mode,
                Source = FindingSource.Policy,
                Message = violation.Evidence is null ? violation.Message : $"{violation.Message} Evidence: {violation.Evidence}",
                GovernanceIds = [record.Id],
                RuleId = result.RuleId,
                RuleVersion = result.RuleVersion,
                Path = path,
                Line = violation.Line,
                WaivedBy = waivedBy,
                Evidence = [recordEvidence, new EvidenceRef("policy-rule", result.RuleId, result.RuleVersion, locator, result.RuleHash)],
                RecommendedAction = waivedBy is not null
                    ? $"No action required while exception {waivedBy} is active."
                    : record.IsNonExemptable
                        ? $"Change the code to comply with {record.Id} ({record.Title}). This is a hard control and cannot be waived."
                        : $"Change the code to comply with {record.Id} ({record.Title}), or request an exception from {owners}.",
            };
        }
    }

    /// <summary>
    /// Canonical form of the input: normalized paths, ordinally ordered changes and added lines, and
    /// oversized content withheld. This is what makes the result independent of input order.
    /// </summary>
    private static PolicyContext Normalize(PolicyContext context, out ImmutableArray<string> oversized)
    {
        var tooLarge = new SortedSet<string>(StringComparer.Ordinal);
        var changes = (context.Changes.IsDefault ? [] : context.Changes)
            .Select(change =>
            {
                var path = GlobPattern.NormalizePath(change.Path);
                var isOversized = PolicyLimits.IsOversized(change.Content) || PolicyLimits.IsOversized(change.PreviousContent);
                if (isOversized)
                {
                    tooLarge.Add(path);
                }

                return change with
                {
                    Path = path,
                    PreviousPath = change.PreviousPath is null ? null : GlobPattern.NormalizePath(change.PreviousPath),
                    Content = isOversized ? null : change.Content,
                    PreviousContent = isOversized ? null : change.PreviousContent,
                    AddedLines = isOversized || change.AddedLines.IsDefault
                        ? []
                        : [.. change.AddedLines.OrderBy(l => l.Number).ThenBy(l => l.Text, StringComparer.Ordinal)],
                };
            })
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.Kind)
            .ThenBy(c => c.PreviousPath, StringComparer.Ordinal)
            .ThenBy(c => c.Content, StringComparer.Ordinal)
            .ThenBy(c => c.PreviousContent, StringComparer.Ordinal)
            .ToImmutableArray();

        oversized = [.. tooLarge];
        return context with { Changes = changes };
    }
}
