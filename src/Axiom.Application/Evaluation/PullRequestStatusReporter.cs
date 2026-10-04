using Axiom.Application.Common;
using Axiom.Domain.Evaluation;
using Microsoft.Extensions.Logging;

namespace Axiom.Application.Evaluation;

/// <summary>
/// Pushes the result of a pull-request evaluation to the source-control system, best effort. A failing
/// provider never changes the verdict; the receipt is authoritative and CI re-reads it.
/// </summary>
public sealed partial class PullRequestStatusReporter(IScmStatusPublisher publisher, ILogger<PullRequestStatusReporter> logger)
{
    public async Task<bool> ReportAsync(StoredEvaluation stored, string? publicBaseUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Evaluation.Stage != EvaluationStage.PullRequest || string.IsNullOrEmpty(stored.Evaluation.Scm.CommitSha))
        {
            return false;
        }

        try
        {
            return await publisher.PublishAsync(ScmStatusReports.For(stored, publicBaseUrl), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AxiomTelemetry.ScmStatusFailures.Add(1);
            LogPublishFailed(logger, stored.Evaluation.Id, ex.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Status for evaluation {EvaluationId} could not be published ({ExceptionType}); the receipt is unaffected.")]
    private static partial void LogPublishFailed(ILogger logger, string evaluationId, string exceptionType);
}
