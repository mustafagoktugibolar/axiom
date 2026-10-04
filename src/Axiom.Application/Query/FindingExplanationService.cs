using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;

namespace Axiom.Application.Query;

public sealed record TraceEntryDto(string RecordId, int Revision, string Kind, bool Selected, ImmutableArray<string> Reasons);

public sealed record AuthorityOwnerDto(string RecordId, string Title, ImmutableArray<string> Owners, bool Exemptable);

/// <summary>Why a finding exists and what resolves it (R19: evidence, selected rules, resolution trace, remediation, owners).</summary>
public sealed record FindingExplanationDto(
    string EvaluationId,
    FindingDto Finding,
    ImmutableArray<PolicyRunDto> SelectedRules,
    ImmutableArray<TraceEntryDto> ResolutionTrace,
    ImmutableArray<string> RemediationOptions,
    ImmutableArray<AuthorityOwnerDto> AuthorityOwners);

public sealed class FindingExplanationService(IEvaluationStore evaluations, GovernanceReadService governance)
{
    public async Task<FindingExplanationDto> ExplainAsync(AxiomPrincipal principal, string evaluationId, string code, string? path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!Authorizer.IsAllowed(principal, AccessRight.ReadAudit))
        {
            Authorizer.Demand(principal, AccessRight.Evaluate);
        }

        var stored = await evaluations.FindAsync(principal.OrganizationId, RequestLimits.Identifier(evaluationId, "evaluationId"), cancellationToken)
            ?? throw AxiomException.NotFound($"Evaluation '{evaluationId}'");
        var evaluation = stored.Evaluation;
        var wanted = RequestLimits.Identifier(code, "code");
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? null : GlobPattern.NormalizePath(path);

        var finding = evaluation.Findings
            .Where(f => string.Equals(f.Code, wanted, StringComparison.Ordinal) && (normalizedPath is null || string.Equals(f.Path, normalizedPath, StringComparison.Ordinal)))
            .OrderByDescending(f => f.Severity)
            .FirstOrDefault()
            ?? throw AxiomException.NotFound($"Finding '{wanted}' on evaluation '{evaluationId}'");

        var ids = finding.GovernanceIds.ToHashSet(StringComparer.Ordinal);
        var rules = evaluation.PolicyRuns
            .Where(r => (finding.RuleId is not null && r.RuleId == finding.RuleId && (ids.Count == 0 || ids.Contains(r.RecordId))) || (finding.RuleId is null && ids.Contains(r.RecordId)))
            .Select(r => new PolicyRunDto(r.RuleId, r.RuleVersion, r.RuleHash, r.RecordId, VerdictLattice.ToContract(r.Mode), r.Outcome.ToString().ToLowerInvariant(), r.Violations.Length, r.Error))
            .ToImmutableArray();

        var trace = evaluation.Trace
            .Where(t => ids.Contains(t.RecordId))
            .Select(t => new TraceEntryDto(t.RecordId, t.Revision, t.Kind.ToString(), t.Selected, t.Reasons))
            .ToImmutableArray();

        var owners = ImmutableArray.CreateBuilder<AuthorityOwnerDto>();
        foreach (var applied in evaluation.AppliedRecords.Where(r => ids.Contains(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var record = await governance.GetRevisionAsync(principal.OrganizationId, applied.Id, applied.Revision, includeRationale: false, cancellationToken);
            if (record is not null)
            {
                owners.Add(new AuthorityOwnerDto(record.Id, record.Title, record.Owners, record.Exemptable));
            }
        }

        var authorities = owners.ToImmutable();
        return new FindingExplanationDto(evaluation.Id, FindingDto.From(finding), rules, trace, Remediation(finding, authorities), authorities);
    }

    private static ImmutableArray<string> Remediation(Finding finding, ImmutableArray<AuthorityOwnerDto> owners)
    {
        var options = new List<string>();
        if (finding.RecommendedAction is { } action)
        {
            options.Add(action);
        }

        if (finding.IsWaived)
        {
            options.Add($"Waived by exception {finding.WaivedBy}; the finding stays visible until the exception ends.");
        }
        else if (finding.Source is FindingSource.Policy or FindingSource.Resolution or FindingSource.Semantic && owners.Length > 0)
        {
            var exemptable = owners.Where(o => o.Exemptable).ToList();
            if (exemptable.Count > 0)
            {
                options.Add($"Request a scoped, expiring exception for {string.Join(", ", exemptable.Select(o => o.RecordId))} from {string.Join(", ", exemptable.SelectMany(o => o.Owners).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}.");
            }

            if (owners.Any(o => !o.Exemptable))
            {
                options.Add($"{string.Join(", ", owners.Where(o => !o.Exemptable).Select(o => o.RecordId))} is a hard control and cannot be waived; change the code instead.");
            }
        }

        if (finding.Severity >= EnforcementLevel.RequireReview && !finding.IsWaived)
        {
            options.Add("Ask a decision owner to review the evaluation (POST /v1/evaluations/{id}/review).");
        }

        return [.. options.Distinct(StringComparer.Ordinal)];
    }
}
