using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class AdrImportTests
{
    private const string Org = "acme";

    private const string Nygard = """
        # 1. Record architecture decisions

        ## Status

        Accepted

        ## Context

        We need to record the architectural decisions made on this project.

        ## Decision

        We will use Architecture Decision Records, as described by Michael Nygard.

        ## Consequences

        See Michael Nygard's article, linked above.
        """;

    private const string Madr = """
        ---
        status: "accepted"
        date: 2026-01-12
        decision-makers: platform-architecture, alice
        ---

        # Use PostgreSQL for persistence

        ## Context and Problem Statement

        Services need a relational store.

        ## Considered Options

        * PostgreSQL
        * MySQL

        ## Decision Outcome

        Chosen option: "PostgreSQL", because it is already operated by the platform team.

        ### Consequences

        * Good, because one engine to run.
        * Bad, because vendor extensions create lock-in.
        """;

    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _admin = new("user:admin", "Admin", Org, [Role.PlatformAdmin], []);

    private AdrImportService Importer(Dictionary<string, string?> files, out FakeTreeSource tree)
    {
        tree = new FakeTreeSource("0123456789abcdef", files);
        return new AdrImportService(tree, new CandidateService(_store, _time));
    }

    [Fact]
    public void Nygard_adrs_are_read_section_by_section()
    {
        var adr = AdrParser.Parse("docs/adr/0001-record.md", Nygard, out var reason)!;

        Assert.Null(reason);
        Assert.Equal("Record architecture decisions", adr.Title);
        Assert.Equal(AdrStatus.Accepted, adr.Status);
        Assert.Equal(AdrFormat.Nygard, adr.Format);
        Assert.StartsWith("We need to record", adr.Context, StringComparison.Ordinal);
        Assert.StartsWith("We will use Architecture Decision Records", adr.Decision, StringComparison.Ordinal);
        Assert.StartsWith("See Michael Nygard", adr.Consequences, StringComparison.Ordinal);
        Assert.Null(adr.Decider);
    }

    [Fact]
    public void Madr_adrs_with_front_matter_nest_consequences_under_the_outcome()
    {
        var adr = AdrParser.Parse("docs/decisions/0002-db.md", Madr, out _)!;

        Assert.Equal("Use PostgreSQL for persistence", adr.Title);
        Assert.Equal(AdrStatus.Accepted, adr.Status);
        Assert.Equal(AdrFormat.Madr, adr.Format);
        Assert.Equal("platform-architecture", adr.Decider);
        Assert.Equal("Services need a relational store.", adr.Context);
        Assert.StartsWith("Chosen option: \"PostgreSQL\"", adr.Decision, StringComparison.Ordinal);
        Assert.DoesNotContain("Good, because", adr.Decision, StringComparison.Ordinal);
        Assert.Contains("lock-in", adr.Consequences, StringComparison.Ordinal);
    }

    [Fact]
    public void Madr_2_status_bullets_and_crlf_line_endings_are_understood()
    {
        const string legacy = "# Use Kafka\r\n\r\n* Status: proposed\r\n* Deciders: carl\r\n\r\n## Context and Problem Statement\r\n\r\nWe need events.\r\n\r\n## Decision Outcome\r\n\r\nChosen option: Kafka.\r\n";
        var adr = AdrParser.Parse("adr/0003-kafka.md", legacy, out _)!;

        Assert.Equal(AdrStatus.Proposed, adr.Status);
        Assert.Equal(AdrFormat.Madr, adr.Format);
        Assert.Equal("Chosen option: Kafka.", adr.Decision);
    }

    [Theory]
    [InlineData("# Title\n\n## Context\n\nOnly context.\n", "no decision section")]
    [InlineData("## Decision\n\nNo title.\n", "no title heading")]
    public void Files_without_a_title_or_decision_are_skipped_with_a_reason(string text, string expected)
    {
        Assert.Null(AdrParser.Parse("docs/adr/x.md", text, out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData("docs/adr/0001-x.md", true)]
    [InlineData("doc/architecture/decisions/0001-x.md", true)]
    [InlineData("ADR/0001-x.md", true)]
    [InlineData("docs/adr/README.md", false)]
    [InlineData("docs/adr/template.md", false)]
    [InlineData("docs/adr/0000-adr-template.md", false)]
    [InlineData("docs/adr/0001-x.txt", false)]
    [InlineData("docs/guide/0001-x.md", false)]
    public void Only_markdown_in_adr_directories_that_is_not_a_template_is_an_adr(string path, bool expected) =>
        Assert.Equal(expected, AdrParser.IsAdrPath(path));

    [Fact]
    public async Task Import_creates_proposed_candidates_with_evidence_and_a_deterministic_method()
    {
        var importer = Importer(new() { ["docs/adr/0001-record.md"] = Nygard, ["docs/decisions/0002-db.md"] = Madr, ["src/App.cs"] = "class A;" }, out var tree);

        var result = await importer.ImportRepositoryAsync(_admin, "repo:gateway", "main", default);

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(2, result.Created);
        Assert.Equal("0123456789abcdef", result.CommitSha);
        Assert.Equal("main", tree.Requests[0].Ref);
        Assert.All(_store.Items, c =>
        {
            Assert.Equal(CandidateState.Proposed, c.State);
            Assert.False(c.IsAuthoritative);
            Assert.Equal(["gateway"], c.Draft.ScopeHints["repositories"].AsEnumerable());
        });
        var nygard = _store.Items.Single(c => c.Draft.Title == "Record architecture decisions");
        Assert.Equal(AdrParser.NygardMethod, nygard.Evidence.ExtractionMethod);
        Assert.Equal("repo:gateway:docs/adr/0001-record.md@0123456789ab", nygard.Evidence.SourceLocator);
        Assert.Equal(0.9, nygard.Evidence.Confidence);
        var madr = _store.Items.Single(c => c.Draft.Title == "Use PostgreSQL for persistence");
        Assert.Equal(AdrParser.MadrMethod, madr.Evidence.ExtractionMethod);
        Assert.Equal("platform-architecture", madr.Evidence.SuggestedOwner);
        Assert.Equal(0.9, madr.Evidence.Confidence);
    }

    [Fact]
    public async Task Importing_twice_is_idempotent_even_when_the_commit_moves()
    {
        var files = new Dictionary<string, string?> { ["docs/adr/0001-record.md"] = Nygard };
        await Importer(files, out _).ImportRepositoryAsync(_admin, "gateway", null, default);

        // Same decision text, different surrounding prose and a new commit: still the same knowledge.
        files["docs/adr/0001-record.md"] = Nygard.Replace("We need to record", "We have to record", StringComparison.Ordinal);
        var second = await new AdrImportService(new FakeTreeSource("fedcba9876543210", files), new CandidateService(_store, _time))
            .ImportRepositoryAsync(_admin, "gateway", null, default);

        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Existing);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task A_changed_decision_is_new_knowledge()
    {
        var files = new Dictionary<string, string?> { ["docs/adr/0001-record.md"] = Nygard };
        await Importer(files, out _).ImportRepositoryAsync(_admin, "gateway", null, default);
        files["docs/adr/0001-record.md"] = Nygard.Replace("as described by Michael Nygard", "with the MADR template", StringComparison.Ordinal);

        var second = await Importer(files, out _).ImportRepositoryAsync(_admin, "gateway", null, default);

        Assert.Equal(1, second.Created);
        Assert.Equal(2, _store.Items.Count);
    }

    [Fact]
    public async Task Rejected_superseded_and_decisionless_adrs_are_reported_not_imported()
    {
        var importer = Importer(new()
        {
            ["docs/adr/0001-a.md"] = Nygard,
            ["docs/adr/0002-b.md"] = Nygard.Replace("Accepted", "Superseded by [ADR-0009](0009.md)", StringComparison.Ordinal).Replace("Architecture Decision Records", "ADRs", StringComparison.Ordinal),
            ["docs/adr/0003-c.md"] = Nygard.Replace("Accepted", "Rejected", StringComparison.Ordinal).Replace("Architecture Decision Records", "wikis", StringComparison.Ordinal),
            ["docs/adr/0004-d.md"] = "# Notes\n\nJust prose.\n",
            ["docs/adr/image.md"] = null,
        }, out _);

        var result = await importer.ImportRepositoryAsync(_admin, "gateway", null, default);

        Assert.Equal(1, result.Created);
        Assert.Equal(4, result.Skipped.Length);
        Assert.Contains(result.Skipped, s => s.Path.EndsWith("0002-b.md", StringComparison.Ordinal) && s.Reason.Contains("superseded", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.Path.EndsWith("0003-c.md", StringComparison.Ordinal) && s.Reason.Contains("rejected", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, s => s.Reason == "no decision section");
        Assert.Contains(result.Skipped, s => s.Reason == "binary or unreadable");
    }

    [Fact]
    public async Task Import_requires_the_source_management_right_and_a_repository()
    {
        var importer = Importer(new() { ["docs/adr/0001-record.md"] = Nygard }, out var tree);
        var reader = new AxiomPrincipal("user:rita", null, Org, [Role.Reader], []);

        await Assert.ThrowsAsync<AxiomException>(() => importer.ImportRepositoryAsync(reader, "gateway", null, default));
        await Assert.ThrowsAsync<AxiomException>(() => importer.ImportRepositoryAsync(_admin, " ", null, default));
        Assert.Empty(tree.Requests);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task Imported_drafts_are_valid_governance_records()
    {
        await Importer(new() { ["docs/decisions/0002-db.md"] = Madr }, out _).ImportRepositoryAsync(_admin, "gateway", null, default);
        var dto = (await new CandidateService(_store, _time).ListAsync(_admin, new CandidateListQuery(null, null, null, 0, 10), default)).Items.Single();

        var parsed = new Axiom.Infrastructure.Governance.Parsing.GovernanceRecordParser().Parse(dto.DraftRecordPath, dto.DraftRecord);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        Assert.Contains("Context", parsed.Record!.Body, StringComparison.Ordinal);
    }
}
