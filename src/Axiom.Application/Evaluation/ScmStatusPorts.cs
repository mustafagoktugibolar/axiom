using System.Text;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;

namespace Axiom.Application.Evaluation;

/// <summary>What a pull-request evaluation tells the source-control system (8.9, 8.10).</summary>
public sealed record ScmStatusReport(
    string OrganizationId,
    string Repository,
    string CommitSha,
    string? PullRequest,
    Verdict Verdict,
    string Title,
    string Summary,
    string? DetailsUrl,
    string EvaluationId);

/// <summary>
/// Publishes a verdict as a status check on the exact commit. The check is a notification: the receipt
/// remains the record of the verdict. Branch protection that requires the check makes CI the enforcement
/// boundary (ADR-0007).
/// </summary>
public interface IScmStatusPublisher
{
    /// <summary>True when the status was accepted by the provider; false when no provider is configured for the repository.</summary>
    Task<bool> PublishAsync(ScmStatusReport report, CancellationToken cancellationToken);
}

/// <summary>Builds the status report of a stored evaluation.</summary>
public static class ScmStatusReports
{
    private const int MaxListed = 10;

    public static ScmStatusReport For(StoredEvaluation stored, string? publicBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var evaluation = stored.Evaluation;
        var active = evaluation.Findings.Where(f => !f.IsWaived && f.Severity >= EnforcementLevel.Warn).ToList();

        var title = evaluation.Verdict switch
        {
            Verdict.Allow => "Axiom: no governance findings",
            Verdict.AllowWithWarnings => $"Axiom: allowed with {active.Count} warning(s)",
            Verdict.RequireReview => "Axiom: review required",
            _ => "Axiom: blocked by governance",
        };

        var summary = new StringBuilder();
        summary.Append("Verdict: ").AppendLine(VerdictLattice.ToContract(evaluation.Verdict));
        summary.Append("Evaluation: ").AppendLine(evaluation.Id);
        summary.Append("Governance snapshot: ").AppendLine(evaluation.SnapshotId);
        foreach (var finding in active.Take(MaxListed))
        {
            summary.Append("- [").Append(VerdictLattice.ToContract(finding.Severity)).Append("] ").Append(finding.Code);
            if (finding.GovernanceIds.Length > 0)
            {
                summary.Append(" (").AppendJoin(", ", finding.GovernanceIds).Append(')');
            }

            if (finding.Path is not null)
            {
                summary.Append(' ').Append(finding.Path);
            }

            summary.AppendLine();
        }

        if (active.Count > MaxListed)
        {
            summary.Append("... and ").Append(active.Count - MaxListed).AppendLine(" more finding(s).");
        }

        var url = string.IsNullOrWhiteSpace(publicBaseUrl) ? null : $"{publicBaseUrl.TrimEnd('/')}/evaluations/{Uri.EscapeDataString(evaluation.Id)}";
        return new ScmStatusReport(
            evaluation.OrganizationId, evaluation.Scm.Repository, evaluation.Scm.CommitSha ?? string.Empty, evaluation.Scm.PullRequest,
            evaluation.Verdict, title, summary.ToString().TrimEnd(), url, evaluation.Id);
    }
}
