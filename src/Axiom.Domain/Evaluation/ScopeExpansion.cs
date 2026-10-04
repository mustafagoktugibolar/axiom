using System.Collections.Immutable;

namespace Axiom.Domain.Evaluation;

/// <summary>What the actual diff touches beyond what an earlier design evaluation covered (R7, design.md §8.3).</summary>
public sealed record ScopeExpansion(
    ImmutableArray<string> NewScope,
    ImmutableArray<string> NewChangeClasses,
    bool SignificantWithoutSignificantDesign)
{
    public bool IsExpanded => !NewScope.IsEmpty || !NewChangeClasses.IsEmpty || SignificantWithoutSignificantDesign;

    public static ScopeExpansion None { get; } = new([], [], false);
}

/// <summary>
/// Compares the scope resolved from the actual diff with the scope of the validated design it claims
/// to implement. The diff is authoritative: anything it adds that the design did not cover is expansion,
/// and expansion means the earlier approval no longer covers the change.
/// </summary>
public static class ScopeExpansionAnalyzer
{
    private static readonly string[] Tracked = ["system", "component", "repository", "domain", "capability", "api", "resource", "technology"];

    private static readonly ImmutableHashSet<string> NonSignificantClasses = [ChangeClasses.DocsOnly, ChangeClasses.TestsOnly];

    /// <param name="approved">The design evaluation the diff continues.</param>
    /// <param name="actualScope">Scope recomputed from the actual changed paths.</param>
    /// <param name="actual">Significance of the actual diff.</param>
    /// <param name="classificationOverridden">A reviewer ruled the change not significant; the design lineage then needs no formal design.</param>
    public static ScopeExpansion Analyze(EvaluationRecord approved, ResolvedScopeView actualScope, SignificanceAssessment actual, bool classificationOverridden)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(actualScope);
        ArgumentNullException.ThrowIfNull(actual);

        var newScope = ImmutableArray.CreateBuilder<string>();
        foreach (var dimension in Tracked)
        {
            if (!actualScope.Dimensions.TryGetValue(dimension, out var values))
            {
                continue;
            }

            var covered = approved.ResolvedScope.Dimensions.TryGetValue(dimension, out var known)
                ? known.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                : ImmutableHashSet<string>.Empty;
            newScope.AddRange(values.Where(v => !covered.Contains(v)).Order(StringComparer.Ordinal).Select(v => $"{dimension}: {v}"));
        }

        if (classificationOverridden || !actual.IsSignificant)
        {
            return new ScopeExpansion([.. newScope], [], false);
        }

        var coveredClasses = approved.Significance.ChangeClasses.ToImmutableHashSet(StringComparer.Ordinal);
        var newClasses = actual.ChangeClasses
            .Where(c => !NonSignificantClasses.Contains(c) && !coveredClasses.Contains(c))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        return new ScopeExpansion([.. newScope], approved.Significance.IsSignificant ? newClasses : [], !approved.Significance.IsSignificant);
    }
}
