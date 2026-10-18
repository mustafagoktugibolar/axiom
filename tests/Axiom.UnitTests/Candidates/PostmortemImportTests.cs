using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class PostmortemImportTests
{
    private const string Org = "acme";

    private const string Postmortem = """
        # Postmortem: checkout outage 2026-09-12

        ## Summary

        Checkout was down for 40 minutes. Everyone must have been very tired.

        ## Root cause

        The payment client had no timeout. Outbound calls must never block without a deadline.

        ## Action items

        - [ ] Add a timeout to every outbound call (owner: alice)
        - [x] Ensure all payment retries are idempotent
        - Alerts should page the on-call within five minutes.
        - Write the incident summary.

        ## Lessons learned

        Retries need to be bounded. We always load test before a release.
        """;

    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _admin = new("user:admin", "Admin", Org, [Role.PlatformAdmin], []);

    private PostmortemImportService Importer(Dictionary<string, string?> files) =>
        new(new FakeTreeSource("0123456789abcdef", files), new CandidateService(_store, _time));

    [Theory]
    [InlineData("docs/postmortems/2026-09-checkout.md", true)]
    [InlineData("incidents/INC-42.md", true)]
    [InlineData("docs/2026-09-postmortem-checkout.md", true)]
    [InlineData("docs/postmortems/README.md", false)]
    [InlineData("docs/postmortems/template.md", false)]
    [InlineData("docs/guide.md", false)]
    [InlineData("incidents/chart.png", false)]
    public void Postmortem_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, PostmortemParser.IsPostmortemPath(path));

    [Fact]
    public void Only_the_root_cause_action_and_lesson_sections_are_read()
    {
        var document = PostmortemParser.Parse(Postmortem);

        Assert.Equal("Postmortem: checkout outage 2026-09-12", document.Title);
        Assert.DoesNotContain(document.Statements, s => s.Original.Contains("tired", StringComparison.Ordinal));
        Assert.Equal(
            [PostmortemSection.RootCause, PostmortemSection.ActionItems, PostmortemSection.ActionItems, PostmortemSection.ActionItems, PostmortemSection.Lessons, PostmortemSection.Lessons],
            document.Statements.Select(s => s.Section));
    }

    [Fact]
    public void Imperative_and_soft_sentences_become_must_statements_and_keep_the_original()
    {
        var statements = PostmortemParser.Parse(Postmortem).Statements;

        var timeout = statements.Single(s => s.Original.StartsWith("Add a timeout", StringComparison.Ordinal));
        Assert.Equal("Teams must add a timeout to every outbound call.", timeout.Statement);
        Assert.True(timeout.Rewritten);
        Assert.Equal(0.4, timeout.Confidence);

        var alerts = statements.Single(s => s.Original.StartsWith("Alerts", StringComparison.Ordinal));
        Assert.Equal("Alerts must page the on-call within five minutes.", alerts.Statement);

        var explicitRule = statements.Single(s => s.Section == PostmortemSection.RootCause);
        Assert.Equal("Outbound calls must never block without a deadline.", explicitRule.Statement);
        Assert.False(explicitRule.Rewritten);

        Assert.DoesNotContain(statements, s => s.Original.Contains("incident summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_stores_candidates_with_the_file_and_section_as_evidence()
    {
        var importer = Importer(new() { ["docs/postmortems/checkout.md"] = Postmortem, ["docs/postmortems/empty.md"] = "# Empty\n\nNothing." });

        var result = await importer.ImportRepositoryAsync(_admin, "gateway", null, null, default);

        Assert.Equal((2, 6, 6), (result.FilesScanned, result.StatementsFound, result.Created));
        Assert.Contains(result.Skipped, s => s.Path.EndsWith("empty.md", StringComparison.Ordinal));
        Assert.All(_store.Items, c =>
        {
            Assert.Equal(PostmortemParser.Method, c.Evidence.ExtractionMethod);
            Assert.False(c.IsAuthoritative);
            Assert.Equal(CandidateState.Proposed, c.State);
            Assert.StartsWith("gateway:docs/postmortems/checkout.md#", c.Evidence.SourceLocator, StringComparison.Ordinal);
        });
        var timeout = _store.Items.Single(c => c.Draft.Statement.StartsWith("Teams must add a timeout", StringComparison.Ordinal));
        Assert.Contains("#action-items@", timeout.Evidence.SourceLocator, StringComparison.Ordinal);
        Assert.Contains("Add a timeout to every outbound call", timeout.Draft.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reimporting_is_idempotent_and_a_path_prefix_limits_the_scan()
    {
        var files = new Dictionary<string, string?> { ["docs/postmortems/a.md"] = Postmortem, ["other/incidents/b.md"] = Postmortem.Replace("checkout", "search", StringComparison.Ordinal) };
        var importer = Importer(files);
        var first = await importer.ImportRepositoryAsync(_admin, "gateway", null, "docs/postmortems", default);
        var second = await importer.ImportRepositoryAsync(_admin, "gateway", null, null, default);

        Assert.Equal(1, first.FilesScanned);
        Assert.Equal(6, first.Created);
        Assert.Equal((0, 6), (second.Created, second.Existing));
        Assert.Equal(6, _store.Items.Count);
    }
}
