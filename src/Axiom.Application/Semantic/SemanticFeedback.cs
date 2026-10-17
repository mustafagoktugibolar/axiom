using System.Collections.Immutable;
using Axiom.Domain.Evaluation;

namespace Axiom.Application.Semantic;

/// <summary>A semantic finding as it was raised, flattened for calibration (task 7.11).</summary>
public sealed record SemanticFindingObservation(
    string EvaluationId,
    string Code,
    string Provider,
    string Model,
    double Confidence,
    ImmutableArray<string> GovernanceIds,
    DateTimeOffset RaisedAt);

/// <summary>
/// A reviewer's overturn of a semantic finding (task 7.10): everything needed to calibrate, joined from the
/// append-only review decision and the immutable evaluation document the finding lives in.
/// </summary>
public sealed record SemanticFeedback(
    string DecisionId,
    string EvaluationId,
    string Code,
    string Provider,
    string Model,
    double Confidence,
    ImmutableArray<string> GovernanceIds,
    string Reviewer,
    string Comment,
    DateTimeOffset DecidedAt);

/// <summary>Read side over raised semantic findings and their overturns. No separate table: see EfSemanticFeedbackStore.</summary>
public interface ISemanticFeedbackStore
{
    /// <summary>Semantic findings of evaluations created in [since, until), oldest first, at most <paramref name="limit"/>.</summary>
    Task<ImmutableArray<SemanticFindingObservation>> ListRaisedAsync(string organizationId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken);

    /// <summary>Overturns of semantic findings decided in [since, until), oldest first, at most <paramref name="limit"/>.</summary>
    Task<ImmutableArray<SemanticFeedback>> ListOverturnsAsync(string organizationId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken);
}

public static class SemanticFindingInfo
{
    public const string Unknown = "unknown";

    /// <summary>Provider and model of a semantic finding, from the <c>model</c> evidence reference the analyzer attaches ("provider/model").</summary>
    public static (string Provider, string Model) ProviderOf(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var id = finding.Evidence.FirstOrDefault(e => string.Equals(e.Source, "model", StringComparison.Ordinal))?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return (Unknown, Unknown);
        }

        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? (id, Unknown) : (id[..slash], id[(slash + 1)..]);
    }
}
