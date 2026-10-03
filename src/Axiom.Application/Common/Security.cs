using System.Collections.Immutable;

namespace Axiom.Application.Common;

/// <summary>Roles required by R22.</summary>
public enum Role
{
    Reader,
    Contributor,
    DecisionOwner,
    Approver,
    ExceptionApprover,
    PlatformAdmin,
    Auditor,
}

public enum AccessRight
{
    ReadGovernance,
    ReadCatalog,
    Evaluate,
    ProposeDecision,
    RequestException,
    ReviewDesign,
    ApproveDecision,
    ApproveException,
    OverrideClassification,
    ManageCatalog,
    ManageGovernanceSource,
    ReadAudit,
    SubmitFeedback,
}

/// <summary>
/// The authenticated caller, as established by the host from an OIDC token or workload identity.
/// Application use cases authorize against this; hosts never bypass it (structure.md).
/// </summary>
public sealed record AxiomPrincipal(string Subject, string? DisplayName, string OrganizationId, ImmutableHashSet<Role> Roles, ImmutableHashSet<string> Teams)
{
    public bool IsInRole(Role role) => Roles.Contains(role);
}

/// <summary>Role → access right mapping and tenant boundary enforcement (R22, R23).</summary>
public static class Authorizer
{
    private static readonly ImmutableDictionary<Role, ImmutableHashSet<AccessRight>> Grants = new Dictionary<Role, ImmutableHashSet<AccessRight>>
    {
        [Role.Reader] = [AccessRight.ReadGovernance, AccessRight.ReadCatalog],
        [Role.Contributor] =
        [
            AccessRight.ReadGovernance, AccessRight.ReadCatalog, AccessRight.Evaluate, AccessRight.ProposeDecision,
            AccessRight.RequestException, AccessRight.SubmitFeedback,
        ],
        [Role.DecisionOwner] =
        [
            AccessRight.ReadGovernance, AccessRight.ReadCatalog, AccessRight.Evaluate, AccessRight.ProposeDecision,
            AccessRight.RequestException, AccessRight.ReviewDesign, AccessRight.OverrideClassification, AccessRight.SubmitFeedback, AccessRight.ReadAudit,
        ],
        [Role.Approver] =
        [
            AccessRight.ReadGovernance, AccessRight.ReadCatalog, AccessRight.ReviewDesign, AccessRight.ApproveDecision,
            AccessRight.OverrideClassification, AccessRight.SubmitFeedback, AccessRight.ReadAudit,
        ],
        [Role.ExceptionApprover] = [AccessRight.ReadGovernance, AccessRight.ReadCatalog, AccessRight.ApproveException, AccessRight.ReadAudit],
        [Role.PlatformAdmin] =
        [
            AccessRight.ReadGovernance, AccessRight.ReadCatalog, AccessRight.Evaluate, AccessRight.ManageCatalog,
            AccessRight.ManageGovernanceSource, AccessRight.ReadAudit,
        ],
        [Role.Auditor] = [AccessRight.ReadGovernance, AccessRight.ReadCatalog, AccessRight.ReadAudit],
    }.ToImmutableDictionary();

    public static bool IsAllowed(AxiomPrincipal principal, AccessRight right)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Roles.Any(role => Grants[role].Contains(right));
    }

    /// <summary>
    /// Throws unless the caller holds the access right and the request targets the caller's own organization.
    /// The organization is always taken from the authenticated principal, never trusted from the request body.
    /// </summary>
    public static void Demand(AxiomPrincipal principal, AccessRight right, string? requestedOrganization = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (requestedOrganization is not null && !string.Equals(requestedOrganization, principal.OrganizationId, StringComparison.Ordinal))
        {
            throw AxiomException.Forbidden("The request targets an organization the caller does not belong to.");
        }

        if (!IsAllowed(principal, right))
        {
            throw AxiomException.Forbidden($"The caller lacks the '{right}' access right.");
        }
    }
}
