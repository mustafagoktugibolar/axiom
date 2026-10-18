using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Policy.Custom;

/// <summary>
/// Evaluates a bound <see cref="CustomRuleSpec"/> against a change. It is a pure function of its inputs
/// and bounded by a step budget (characters and nodes examined), regex match timeouts and a violation
/// cap, so a hostile definition or hostile repository content cannot run away.
/// </summary>
public static class CustomRuleEvaluator
{
    public const long DefaultStepBudget = 50_000_000;
    public const int MaxViolations = 500;
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static ImmutableArray<PolicyViolation> Evaluate(
        CustomRuleSpec spec,
        IEnumerable<ChangedFile> changes,
        IReadOnlyCollection<string> treePaths,
        long stepBudget = DefaultStepBudget)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(treePaths);

        var run = new Run(stepBudget);
        try
        {
            var selected = Select(spec.Selection, changes, run);
            foreach (var check in spec.Checks)
            {
                if (run.Full)
                {
                    break;
                }

                switch (check.Type)
                {
                    case CustomCheckType.ForbidPath:
                        ForbidPath(check, selected, run);
                        break;
                    case CustomCheckType.ForbidRegex:
                        ForbidRegex(check, selected, run);
                        break;
                    case CustomCheckType.RequireRegex:
                        RequireRegex(check, selected, run);
                        break;
                    case CustomCheckType.RequireFile:
                        RequireFile(check, treePaths, run);
                        break;
                    default:
                        JsonPath(check, selected, run);
                        break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new CustomRuleException(CustomRuleCodes.RegexTimeout, "A regular expression exceeded its match timeout.");
        }

        return [.. run.Violations
            .Distinct()
            .OrderBy(v => v.Path, StringComparer.Ordinal)
            .ThenBy(v => v.Line)
            .ThenBy(v => v.Message, StringComparer.Ordinal)];
    }

    private static List<ChangedFile> Select(CustomSelection selection, IEnumerable<ChangedFile> changes, Run run)
    {
        var include = selection.Paths.Select(ParseGlob).ToArray();
        var exclude = selection.Exclude.Select(ParseGlob).ToArray();
        var result = new List<ChangedFile>();
        foreach (var change in changes)
        {
            run.Spend(1 + change.Path.Length);
            var path = GlobPattern.NormalizePath(change.Path);
            if (selection.Kinds.Contains(change.Kind)
                && include.Any(g => g.IsMatch(path))
                && !exclude.Any(g => g.IsMatch(path)))
            {
                result.Add(change);
            }
        }

        // Ordinal order makes the violation cap (and so the result) independent of input order.
        return [.. result.OrderBy(c => GlobPattern.NormalizePath(c.Path), StringComparer.Ordinal)];
    }

    private static void ForbidPath(CustomCheck check, List<ChangedFile> selected, Run run)
    {
        foreach (var change in selected)
        {
            run.Add(new PolicyViolation(check.Message ?? $"'{change.Path}' must not be changed this way.", change.Path));
        }
    }

    private static void ForbidRegex(CustomCheck check, List<ChangedFile> selected, Run run)
    {
        var regex = CustomRuleSpec.CompileRegex(check.Pattern!, RegexTimeout);
        var evidence = $"matches {check.Pattern}";
        foreach (var change in selected)
        {
            if (check.AddedLinesOnly)
            {
                foreach (var line in (change.AddedLines.IsDefault ? [] : change.AddedLines).OrderBy(l => l.Number))
                {
                    run.Spend(line.Text.Length + 1);
                    if (regex.IsMatch(line.Text))
                    {
                        run.Add(new PolicyViolation(check.Message ?? "Forbidden pattern found.", change.Path, line.Number, evidence));
                    }
                }

                continue;
            }

            if (change.Content is null)
            {
                continue;
            }

            var number = 0;
            foreach (var line in change.Content.AsSpan().EnumerateLines())
            {
                number++;
                run.Spend(line.Length + 1);
                if (regex.IsMatch(line))
                {
                    run.Add(new PolicyViolation(check.Message ?? "Forbidden pattern found.", change.Path, number, evidence));
                }
            }
        }
    }

    private static void RequireRegex(CustomCheck check, List<ChangedFile> selected, Run run)
    {
        var regex = CustomRuleSpec.CompileRegex(check.Pattern!, RegexTimeout);
        foreach (var change in selected.Where(c => c.Kind != ChangeKind.Deleted))
        {
            if (change.Content is null)
            {
                run.Add(new PolicyViolation($"'{change.Path}' has no readable content, so the required pattern could not be confirmed.", change.Path));
                continue;
            }

            run.Spend(change.Content.Length + 1);
            if (!regex.IsMatch(change.Content))
            {
                run.Add(new PolicyViolation(check.Message ?? "Required pattern not found.", change.Path, Evidence: $"expected {check.Pattern}"));
            }
        }
    }

    private static void RequireFile(CustomCheck check, IReadOnlyCollection<string> treePaths, Run run)
    {
        var glob = ParseGlob(check.Path!);
        foreach (var path in treePaths)
        {
            run.Spend(path.Length + 1);
            if (glob.IsMatch(path))
            {
                return;
            }
        }

        run.Add(new PolicyViolation(check.Message ?? $"No file matching '{glob.Value}' exists.", Evidence: $"expected {glob.Value}"));
    }

    private static void JsonPath(CustomCheck check, List<ChangedFile> selected, Run run)
    {
        var expression = JsonPathExpression.Parse(check.Path!);
        var regex = check.Operator == JsonPathOperator.Matches ? CustomRuleSpec.CompileRegex(check.Value!, RegexTimeout) : null;
        foreach (var change in selected.Where(c => c.Kind != ChangeKind.Deleted))
        {
            if (change.Content is null)
            {
                run.Add(new PolicyViolation($"'{change.Path}' has no readable content.", change.Path));
                continue;
            }

            run.Spend(change.Content.Length + 1);
            List<JsonElement> nodes;
            try
            {
                using var document = JsonDocument.Parse(change.Content, new JsonDocumentOptions { MaxDepth = PolicyLimits.MaxNestingDepth });
                nodes = expression.Select(document.RootElement, () => run.Spend(1));

                var ok = check.Operator switch
                {
                    JsonPathOperator.Exists => nodes.Count > 0,
                    JsonPathOperator.Absent => nodes.Count == 0,
                    JsonPathOperator.Equal => nodes.Count > 0 && nodes.All(n => Scalar(n) == check.Value),
                    JsonPathOperator.NotEqual => nodes.All(n => Scalar(n) != check.Value),
                    _ => nodes.Count > 0 && nodes.All(n => Scalar(n) is { } s && regex!.IsMatch(s)),
                };

                if (!ok)
                {
                    run.Add(new PolicyViolation(
                        check.Message ?? $"JSON path {check.Path} does not satisfy '{Describe(check.Operator)}'.",
                        change.Path,
                        Evidence: check.Value is null ? check.Path : $"{check.Path} {Describe(check.Operator)} {check.Value}"));
                }
            }
            catch (JsonException)
            {
                run.Add(new PolicyViolation($"'{change.Path}' is not valid JSON, so the JSON path check could not be evaluated.", change.Path));
            }
        }
    }

    private static GlobPattern ParseGlob(string pattern)
    {
        try
        {
            return GlobPattern.Parse(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new CustomRuleException("A path selector is empty or not a valid glob.", ex);
        }
    }

    private static string Describe(JsonPathOperator op) => op switch
    {
        JsonPathOperator.Exists => "exists",
        JsonPathOperator.Absent => "absent",
        JsonPathOperator.Equal => "equals",
        JsonPathOperator.NotEqual => "not-equals",
        _ => "matches",
    };

    private static string? Scalar(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.String => node.GetString(),
        JsonValueKind.Number => node.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => null,
    };

    private sealed class Run(long budget)
    {
        private long _remaining = budget;

        public List<PolicyViolation> Violations { get; } = [];

        public bool Full => Violations.Count >= MaxViolations;

        public void Spend(long cost)
        {
            _remaining -= cost;
            if (_remaining < 0)
            {
                throw new CustomRuleException(CustomRuleCodes.StepBudgetExceeded, "The rule exceeded its step budget.");
            }
        }

        public void Add(PolicyViolation violation)
        {
            if (!Full)
            {
                Violations.Add(violation);
            }
        }
    }
}
