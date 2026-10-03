using Axiom.Domain.Governance;
using Axiom.Infrastructure.Governance.Parsing;

namespace Axiom.UnitTests.Governance;

public class GovernanceRecordParserTests
{
    private readonly GovernanceRecordParser _parser = new();

    private static string SpecExample(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SpecExamples", relative));

    [Fact]
    public void Parses_the_specification_decision_example()
    {
        var parsed = _parser.Parse("governance/decisions/ARCH-042-gateway-routing.md", SpecExample("decisions/ARCH-042-gateway-routing.md"));

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        var record = parsed.Record!;
        Assert.Equal("ARCH-042", record.Id);
        Assert.Equal(RecordKind.Decision, record.Kind);
        Assert.Equal(LifecycleStatus.Accepted, record.Status);
        Assert.Equal(["platform-architecture"], record.Owners.ToArray());
        Assert.Equal(AuthorityLevel.System, record.Authority.Level);
        Assert.True(record.Authority.Exemptable);
        Assert.Equal(EnforcementLevel.RequireReview, record.Enforcement.DefaultVerdict);
        var rule = Assert.Single(record.Enforcement.Rules);
        Assert.Equal("gateway-no-domain-db-access", rule.RuleId);
        Assert.Equal(EnforcementLevel.Block, rule.Mode);
        Assert.Equal(["gateway"], record.Scope[ScopeDimension.Repository]);
        Assert.Equal(["homepage-resolution", "routing"], record.Scope[ScopeDimension.Capability]);
        Assert.False(record.Scope.Restricts(ScopeDimension.Path));
        Assert.Equal(new DateOnly(2026, 9, 1), record.Validity.EffectiveFrom);
        Assert.Equal(new DateOnly(2027, 3, 1), record.Validity.ReviewAfter);
        Assert.Equal(["SYS-GUI-001"], record.RelatedIds(RelationKind.Related));
        Assert.Equal(2, record.Forbidden.Length);
        Assert.StartsWith("## Context", record.Body, StringComparison.Ordinal);
        Assert.Equal(64, record.ContentHash.Length);
    }

    [Fact]
    public void Parses_the_specification_exception_example()
    {
        var parsed = _parser.Parse("governance/exceptions/EXC-023-legacy-homepage.yaml", SpecExample("exceptions/EXC-023-legacy-homepage.yaml"));

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        var record = parsed.Record!;
        Assert.Equal(RecordKind.Exception, record.Kind);
        Assert.Equal(["ARCH-042"], record.Exception!.Targets.ToArray());
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), record.Exception.ExpiresAt);
        Assert.Equal(["platform-architecture"], record.Exception.Approvers.ToArray());
        Assert.Equal("GUI-912", record.Exception.TrackingIssue);
        Assert.Equal(["src/Legacy/Homepage/**"], record.Scope[ScopeDimension.Path]);
        Assert.Equal(["ARCH-042"], record.RelatedIds(RelationKind.ExceptionTo));
    }

    [Fact]
    public void Specification_examples_form_a_valid_set()
    {
        var decision = _parser.Parse("governance/decisions/a.md", SpecExample("decisions/ARCH-042-gateway-routing.md")).Record!;
        var exception = _parser.Parse("governance/exceptions/e.yaml", SpecExample("exceptions/EXC-023-legacy-homepage.yaml")).Record!;

        Assert.DoesNotContain(GovernanceSetValidator.Validate([decision, exception]), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Content_hash_ignores_line_ending_style()
    {
        var text = SpecExample("exceptions/EXC-023-legacy-homepage.yaml").Replace("\r\n", "\n", StringComparison.Ordinal);
        var lf = _parser.Parse("governance/exceptions/e.yaml", text).Record!;
        var crlf = _parser.Parse("governance/exceptions/e.yaml", text.Replace("\n", "\r\n", StringComparison.Ordinal)).Record!;

        Assert.Equal(lf.ContentHash, crlf.ContentHash);
    }

    [Theory]
    [InlineData("status: accepted", "status: approved")]
    [InlineData("level: system", "level: architecture")]
    [InlineData("exemptable: true", "exemptible: true")]
    [InlineData("id: ARCH-042", "id: arch42")]
    [InlineData("owners: [platform-architecture]", "owners: []")]
    public void Schema_violations_are_rejected(string original, string replacement)
    {
        var text = SpecExample("decisions/ARCH-042-gateway-routing.md").Replace(original, replacement, StringComparison.Ordinal);
        var parsed = _parser.Parse("governance/decisions/a.md", text);

        Assert.Null(parsed.Record);
        Assert.All(parsed.Issues, i => Assert.Equal(GovernanceRecordParser.SchemaInvalid, i.Code));
        Assert.NotEmpty(parsed.Issues);
    }

    [Fact]
    public void Record_in_the_wrong_directory_is_an_error()
    {
        var parsed = _parser.Parse("governance/standards/a.md", SpecExample("decisions/ARCH-042-gateway-routing.md"));
        Assert.Contains(parsed.Issues, i => i.Code == GovernanceRecordParser.MisplacedRecord);
        Assert.False(parsed.IsValid);
    }

    [Theory]
    [InlineData("kind: Decision\nkind: Decision\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("kind: [unterminated\n")]
    [InlineData("kind: Policy\n")]
    public void Malformed_documents_fail_without_throwing(string yaml)
    {
        var parsed = _parser.Parse("governance/decisions/x.yaml", yaml);
        Assert.Null(parsed.Record);
        Assert.NotEmpty(parsed.Issues);
    }

    [Theory]
    [InlineData("governance/decisions/ARCH-001.md", true)]
    [InlineData("governance/exceptions/EXC-001.yaml", true)]
    [InlineData("decisions/nested/ARCH-001.yml", true)]
    [InlineData("governance/schemas/decision.schema.json", false)]
    [InlineData("governance/designs/DESIGN-101.md", false)]
    [InlineData("README.md", false)]
    public void Recognizes_record_paths(string path, bool expected) => Assert.Equal(expected, _parser.IsRecordPath(path));
}
