using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Json.Schema;
using YamlDotNet.Core;

namespace Axiom.Infrastructure.Governance.Parsing;

/// <summary>
/// Parses governance source files (YAML, or Markdown with YAML frontmatter), validates them against
/// the published JSON schema for their kind, and maps them to <see cref="GovernanceRecord"/>.
/// </summary>
public sealed class GovernanceRecordParser : IGovernanceRecordParser
{
    public const string SchemaInvalid = "GOV_SCHEMA_INVALID";
    public const string Unparseable = "GOV_UNPARSEABLE";
    public const string UnknownKind = "GOV_UNKNOWN_KIND";
    public const string MisplacedRecord = "GOV_KIND_DIRECTORY_MISMATCH";

    private static readonly ImmutableDictionary<string, RecordKind> KindDirectories = new Dictionary<string, RecordKind>(StringComparer.Ordinal)
    {
        ["principles"] = RecordKind.Principle,
        ["standards"] = RecordKind.Standard,
        ["decisions"] = RecordKind.Decision,
        ["goals"] = RecordKind.Goal,
        ["exceptions"] = RecordKind.Exception,
    }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<RecordKind, JsonSchema> Schemas =
        Enum.GetValues<RecordKind>().ToImmutableDictionary(k => k, LoadSchema);

    private static readonly ImmutableDictionary<string, ScopeDimension> ScopeKeys = new Dictionary<string, ScopeDimension>(StringComparer.Ordinal)
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
    }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<string, RelationKind> RelationKeys = new Dictionary<string, RelationKind>(StringComparer.Ordinal)
    {
        ["supersedes"] = RelationKind.Supersedes,
        ["supersededBy"] = RelationKind.SupersededBy,
        ["refines"] = RelationKind.Refines,
        ["implements"] = RelationKind.Implements,
        ["conflictsWith"] = RelationKind.ConflictsWith,
        ["related"] = RelationKind.Related,
        ["motivatedBy"] = RelationKind.MotivatedBy,
        ["requires"] = RelationKind.Requires,
        ["exceptionTo"] = RelationKind.ExceptionTo,
    }.ToImmutableDictionary();

    public bool IsRecordPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = GlobPattern.NormalizePath(path);
        var extension = Path.GetExtension(normalized);
        return extension is ".md" or ".yaml" or ".yml" && KindFromPath(normalized) is not null;
    }

    public ParsedRecord Parse(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);

        var canonical = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var (yaml, body) = SplitFrontmatter(path, canonical);

        JsonNode? document;
        try
        {
            document = YamlToJson.Convert(yaml);
        }
        catch (YamlException ex)
        {
            return Failure(ValidationIssue.Error(Unparseable, $"YAML could not be parsed: {ex.Message}", sourcePath: path));
        }

        if (document is not JsonObject root)
        {
            return Failure(ValidationIssue.Error(Unparseable, "Governance record must be a YAML mapping.", sourcePath: path));
        }

        var kindText = root["kind"] is JsonValue kindValue && kindValue.TryGetValue<string>(out var k) ? k : null;
        if (kindText is null || !Enum.TryParse<RecordKind>(kindText, ignoreCase: false, out var kind))
        {
            return Failure(ValidationIssue.Error(UnknownKind, $"Unknown or missing record kind '{kindText}'.", sourcePath: path));
        }

        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        var recordId = (root["metadata"] as JsonObject)?["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) ? id : null;

        var evaluation = Schemas[kind].Evaluate(JsonSerializer.SerializeToElement(root), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!evaluation.IsValid)
        {
            foreach (var detail in (evaluation.Details ?? []).Where(d => d.Errors is { Count: > 0 }).OrderBy(d => d.InstanceLocation.ToString(), StringComparer.Ordinal))
            {
                foreach (var (keyword, message) in detail.Errors!.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    issues.Add(ValidationIssue.Error(SchemaInvalid, $"{detail.InstanceLocation}: [{keyword}] {message}", recordId, path));
                }
            }

            if (issues.Count == 0)
            {
                issues.Add(ValidationIssue.Error(SchemaInvalid, "Record does not conform to its schema.", recordId, path));
            }

            return new ParsedRecord(null, issues.ToImmutable());
        }

        if (KindFromPath(GlobPattern.NormalizePath(path)) is { } expected && expected != kind)
        {
            issues.Add(ValidationIssue.Error(MisplacedRecord, $"Record kind '{kind}' is stored in the '{expected}' directory.", recordId, path));
        }

        try
        {
            var record = Map(kind, root, body, Hash(canonical));
            return new ParsedRecord(record, issues.ToImmutable());
        }
        catch (FormatException ex)
        {
            issues.Add(ValidationIssue.Error(SchemaInvalid, ex.Message, recordId, path));
            return new ParsedRecord(null, issues.ToImmutable());
        }
    }

    private static ParsedRecord Failure(ValidationIssue issue) => new(null, [issue]);

    private static RecordKind? KindFromPath(string normalizedPath)
    {
        var segments = normalizedPath.Split('/');
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (KindDirectories.TryGetValue(segments[i], out var kind))
            {
                return kind;
            }
        }

        return null;
    }

    private static (string Yaml, string Body) SplitFrontmatter(string path, string content)
    {
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return (content, string.Empty);
        }

        const string fence = "---";
        var text = content.TrimStart('﻿');
        if (!text.StartsWith(fence + "\n", StringComparison.Ordinal))
        {
            return (string.Empty, text);
        }

        var end = text.IndexOf("\n" + fence, fence.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return (string.Empty, text);
        }

        var yaml = text[(fence.Length + 1)..(end + 1)];
        var rest = text[(end + 1 + fence.Length)..];
        return (yaml, rest.TrimStart('\n').TrimEnd());
    }

    private static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static GovernanceRecord Map(RecordKind kind, JsonObject root, string body, string contentHash)
    {
        var metadata = root["metadata"]!.AsObject();
        var spec = root["spec"]!.AsObject();
        var isException = kind == RecordKind.Exception;

        return new GovernanceRecord
        {
            Id = Text(metadata, "id")!,
            Kind = kind,
            SchemaVersion = Text(root, "schemaVersion")!,
            Title = Text(metadata, "title")!,
            Owners = Strings(metadata, "owners"),
            Tags = Strings(metadata, "tags"),
            Status = ParseStatus(Text(spec, "status")!),
            Scope = MapScope(spec["scope"] as JsonObject),
            Authority = isException
                ? new Authority(AuthorityLevel.Advisory, Exemptable: true)
                : MapAuthority(spec["authority"]!.AsObject()),
            Enforcement = isException
                ? new Enforcement(EnforcementLevel.Info, [])
                : MapEnforcement(spec["enforcement"]!.AsObject()),
            Statement = (isException ? Text(spec, "rationale") : Text(spec, kind == RecordKind.Decision ? "decision" : "statement"))!.Trim(),
            Forbidden = Strings(spec, "forbidden"),
            Preferred = Strings(spec, "preferred"),
            Relations = MapRelations(spec["relationships"] as JsonObject, isException ? Strings(spec, "targets") : []),
            Validity = MapValidity(spec["validity"] as JsonObject),
            Exception = isException ? MapException(spec) : null,
            GovernsExistingCode = spec["governsExistingCode"]?.GetValue<bool>() ?? false,
            Migration = Text(spec, "migration") switch
            {
                null => null,
                "immediate" => MigrationMode.Immediate,
                "new-code-only" => MigrationMode.NewCodeOnly,
                "phased" => MigrationMode.Phased,
                "date-bound" => MigrationMode.DateBound,
                var other => throw new FormatException($"Unknown migration mode '{other}'."),
            },
            SemanticExternalAllowed = spec["semanticExternalAllowed"]?.GetValue<bool>() ?? true,
            Evidence = MapEvidence(spec["evidence"] as JsonArray),
            Body = body,
            ContentHash = contentHash,
        };
    }

    private static LifecycleStatus ParseStatus(string status) => status switch
    {
        "proposed" => LifecycleStatus.Proposed,
        "accepted" => LifecycleStatus.Accepted,
        "deprecated" => LifecycleStatus.Deprecated,
        "superseded" => LifecycleStatus.Superseded,
        "rejected" => LifecycleStatus.Rejected,
        "expired" => LifecycleStatus.Expired,
        _ => throw new FormatException($"Unknown lifecycle status '{status}'."),
    };

    internal static EnforcementLevel ParseLevel(string level) => level switch
    {
        "info" => EnforcementLevel.Info,
        "warn" => EnforcementLevel.Warn,
        "require_review" => EnforcementLevel.RequireReview,
        "block" => EnforcementLevel.Block,
        _ => throw new FormatException($"Unknown enforcement level '{level}'."),
    };

    private static Scope MapScope(JsonObject? scope)
    {
        if (scope is null)
        {
            return Scope.Unrestricted;
        }

        return Scope.Create(scope
            .Where(kv => ScopeKeys.ContainsKey(kv.Key))
            .Select(kv => new KeyValuePair<ScopeDimension, IEnumerable<string>>(ScopeKeys[kv.Key], Strings(scope, kv.Key))));
    }

    private static Authority MapAuthority(JsonObject authority)
    {
        var level = Text(authority, "level") switch
        {
            "regulatory-security-hard" => AuthorityLevel.RegulatorySecurityHard,
            "organization" => AuthorityLevel.Organization,
            "domain" => AuthorityLevel.Domain,
            "system" => AuthorityLevel.System,
            "component" => AuthorityLevel.Component,
            "repository" => AuthorityLevel.Repository,
            "advisory" => AuthorityLevel.Advisory,
            var other => throw new FormatException($"Unknown authority level '{other}'."),
        };
        return new Authority(level, authority["exemptable"]!.GetValue<bool>());
    }

    private static Enforcement MapEnforcement(JsonObject enforcement)
    {
        var rules = (enforcement["rules"] as JsonArray ?? [])
            .Select(r => r!.AsObject())
            .Select(r => new RuleBinding(
                Text(r, "ruleId")!,
                ParseLevel(Text(r, "mode")!),
                (r["with"] as JsonObject ?? []).ToImmutableSortedDictionary(kv => kv.Key, kv => ScalarText(kv.Value), StringComparer.Ordinal)))
            .ToImmutableArray();
        return new Enforcement(ParseLevel(Text(enforcement, "defaultVerdict")!), rules);
    }

    private static ImmutableArray<Relation> MapRelations(JsonObject? relationships, ImmutableArray<string> exceptionTargets)
    {
        var relations = ImmutableArray.CreateBuilder<Relation>();
        foreach (var target in exceptionTargets)
        {
            relations.Add(new Relation(RelationKind.ExceptionTo, target));
        }

        if (relationships is not null)
        {
            foreach (var (key, kind) in RelationKeys.OrderBy(kv => kv.Value))
            {
                foreach (var target in Strings(relationships, key))
                {
                    relations.Add(new Relation(kind, target));
                }
            }
        }

        return relations.Distinct().ToImmutableArray();
    }

    private static Validity MapValidity(JsonObject? validity) => validity is null
        ? Validity.Unbounded
        : new Validity(Date(validity, "effectiveFrom"), Date(validity, "effectiveUntil"), Date(validity, "reviewAfter"));

    private static ExceptionTerms MapException(JsonObject spec) => new(
        Strings(spec, "targets"),
        Text(spec, "rationale")!.Trim(),
        Instant(spec, "startsAt"),
        Instant(spec, "expiresAt"),
        Strings(spec, "approvers"),
        Text(spec, "trackingIssue"),
        Strings(spec, "compensatingControls"));

    private static ImmutableArray<CandidateEvidence> MapEvidence(JsonArray? evidence) => evidence is null
        ? []
        : evidence.Select(e => e!.AsObject())
            .Select(e => new CandidateEvidence(
                Text(e, "source")!,
                Text(e, "excerptHash")!,
                Text(e, "method")!,
                e["confidence"]!.GetValue<double>(),
                Text(e, "suggestedOwner")))
            .ToImmutableArray();

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value ? ScalarText(value) : null;

    private static string ScalarText(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("R", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static ImmutableArray<string> Strings(JsonObject node, string name) =>
        node[name] is JsonArray array
            ? array.Select(ScalarText).Where(s => s.Length > 0).ToImmutableArray()
            : [];

    private static DateOnly? Date(JsonObject node, string name) =>
        Text(node, name) is { } text
            ? DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new FormatException($"'{name}' must be a calendar date (yyyy-MM-dd), got '{text}'.")
            : null;

    private static DateTimeOffset Instant(JsonObject node, string name) =>
        DateTimeOffset.TryParse(Text(node, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : throw new FormatException($"'{name}' must be an RFC 3339 timestamp.");

    private static JsonSchema LoadSchema(RecordKind kind)
    {
        var name = $"Axiom.Schemas.{kind.ToString().ToLowerInvariant()}.schema.json";
        using var stream = typeof(GovernanceRecordParser).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded governance schema '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
