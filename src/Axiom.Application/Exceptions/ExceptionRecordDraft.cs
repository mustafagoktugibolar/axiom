using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Axiom.Domain.Governance;

namespace Axiom.Application.Exceptions;

/// <summary>Renders the governance record a request would become. The Git repository stays the only source of authority (ADR-0001, ADR-0006).</summary>
public static class ExceptionRecordDraft
{
    public static string PathFor(string draftId) => $"governance/exceptions/{draftId}.yaml";

    /// <param name="status">"proposed" for an open request, "accepted" once approved.</param>
    /// <param name="approvers">Approvers named in the record; only meaningful when accepted.</param>
    public static string Render(ExceptionRequest request, string status, IReadOnlyCollection<string> approvers)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approvers);
        var yaml = new StringBuilder();
        yaml.AppendLine("schemaVersion: axiom.io/v1");
        yaml.AppendLine("kind: Exception");
        yaml.AppendLine("metadata:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  id: {request.DraftId}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  title: {Quote(request.Title)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  owners: {Flow(request.Owners)}");
        yaml.AppendLine("spec:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  status: {status}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  targets: {Flow(request.Targets)}");
        yaml.AppendLine("  scope:");
        foreach (var (dimension, values) in request.Scope)
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    {dimension}: {Flow(values)}");
        }

        yaml.AppendLine(CultureInfo.InvariantCulture, $"  rationale: {Quote(request.Rationale)}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  startsAt: {request.StartsAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  expiresAt: {request.ExpiresAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"  approvers: {Flow([.. approvers])}");
        if (request.TrackingIssue is not null)
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"  trackingIssue: {Quote(request.TrackingIssue)}");
        }

        if (!request.CompensatingControls.IsEmpty)
        {
            yaml.AppendLine("  compensatingControls:");
            foreach (var control in request.CompensatingControls)
            {
                yaml.AppendLine(CultureInfo.InvariantCulture, $"    - {Quote(control)}");
            }
        }

        return yaml.ToString();
    }

    /// <summary>The candidate as a domain record, for validation by the same rules that guard the Git repository.</summary>
    public static GovernanceRecord ToRecord(ExceptionRequest request, LifecycleStatus status, IReadOnlyCollection<string> approvers) => new()
    {
        Id = request.DraftId,
        Kind = RecordKind.Exception,
        SchemaVersion = "axiom.io/v1",
        Title = request.Title,
        Owners = request.Owners,
        Status = status,
        Scope = Scope.Create(request.Scope.Select(kv => new KeyValuePair<ScopeDimension, IEnumerable<string>>(ExceptionScopeKeys.Dimension(kv.Key)!.Value, kv.Value))),
        Authority = new Authority(AuthorityLevel.Advisory, true),
        Enforcement = new Enforcement(EnforcementLevel.Info, []),
        Statement = request.Rationale,
        Relations = [.. request.Targets.Select(t => new Relation(RelationKind.ExceptionTo, t))],
        Exception = new ExceptionTerms(
            request.Targets, request.Rationale, request.StartsAt, request.ExpiresAt, [.. approvers], request.TrackingIssue, request.CompensatingControls),
        ContentHash = "draft",
    };

    private static string Flow(ImmutableArray<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";

    // JSON string syntax is valid YAML double-quoted syntax, so no content can break out of its field.
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}

/// <summary>The plural dimension names used in governance records.</summary>
public static class ExceptionScopeKeys
{
    private static readonly ImmutableDictionary<string, ScopeDimension> Keys = new Dictionary<string, ScopeDimension>(StringComparer.OrdinalIgnoreCase)
    {
        ["organizations"] = ScopeDimension.Organization,
        ["domains"] = ScopeDimension.Domain,
        ["systems"] = ScopeDimension.System,
        ["components"] = ScopeDimension.Component,
        ["repositories"] = ScopeDimension.Repository,
        ["paths"] = ScopeDimension.Path,
        ["capabilities"] = ScopeDimension.Capability,
        ["apis"] = ScopeDimension.Api,
        ["resources"] = ScopeDimension.Resource,
        ["technologies"] = ScopeDimension.Technology,
        ["environments"] = ScopeDimension.Environment,
        ["changeClasses"] = ScopeDimension.ChangeClass,
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static ScopeDimension? Dimension(string key) => Keys.TryGetValue(key, out var d) ? d : null;

    public static string Canonical(string key) => Keys.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;

    public static IEnumerable<string> Names => Keys.Keys.Order(StringComparer.Ordinal);
}
