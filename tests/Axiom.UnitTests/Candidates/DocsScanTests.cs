using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Application.Exceptions;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class DocsScanTests
{
    private const string Org = "acme";

    private const string Readme = """
        # Gateway

        ## Decisions

        We decided to use gRPC for all internal calls.

        ## Notes

        Services must not call the database of another service directly. Deploys happen weekly.

        - Clients should always send a correlation id.
        - The v1 session API is deprecated and will be removed in Q4.

        ```
        We decided this is inside a code fence and must be ignored.
        ```

        Is this something we must never ask?
        """;

    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _admin = new("user:admin", "Admin", Org, [Role.PlatformAdmin], []);

    private DocsScanService Scanner(Dictionary<string, string?> files, out FakeTreeSource tree)
    {
        tree = new FakeTreeSource("0123456789abcdef", files);
        return new DocsScanService(tree, new CandidateService(_store, _time));
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("services/api/README.md", true)]
    [InlineData("docs/runbook/deploy.md", true)]
    [InlineData("ARCHITECTURE.md", true)]
    [InlineData("docs/adr/0001-use-grpc.md", false)]
    [InlineData("docs/templates/template.md", false)]
    [InlineData("src/notes.md", false)]
    [InlineData("docs/node_modules/x/README.md", false)]
    [InlineData("docs/diagram.png", false)]
    public void Only_documentation_paths_are_scanned(string path, bool expected) =>
        Assert.Equal(expected, DocsScanner.IsDocPath(path));

    [Fact]
    public void Phrases_are_matched_with_a_named_method_and_confidence()
    {
        var statements = DocsScanner.Scan("README.md", Readme);

        Assert.Equal(4, statements.Length);
        var decided = statements.Single(s => s.Method == DocsScanner.DecisionMethod);
        Assert.Equal("We decided to use gRPC for all internal calls.", decided.Sentence);
        Assert.Equal("Decisions", decided.Heading);
        Assert.Equal(0.7, decided.Confidence);
        var mustNot = statements.Single(s => s.Phrase == "must not");
        Assert.Equal("Services must not call the database of another service directly.", mustNot.Sentence);
        Assert.Equal(0.5, mustNot.Confidence);
        Assert.Single(statements, s => s.Method == DocsScanner.DeprecationMethod);
        Assert.Single(statements, s => s.Phrase == "always");
    }

    [Fact]
    public void Code_fences_and_questions_are_ignored()
    {
        var statements = DocsScanner.Scan("README.md", Readme);

        Assert.DoesNotContain(statements, s => s.Sentence.Contains("code fence", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, s => s.Sentence.Contains("ask", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scanning_stores_proposed_candidates_that_never_govern()
    {
        var scanner = Scanner(new() { ["README.md"] = Readme, ["docs/adr/0001-x.md"] = "# 1. X\n\n## Decision\n\nWe decided x everything." }, out var tree);

        var result = await scanner.ScanRepositoryAsync(_admin, "gateway", "main", default);

        Assert.Equal(("gateway", 1, 4, 4, 0), (result.Repository, result.FilesScanned, result.StatementsFound, result.Created, result.Existing));
        Assert.Equal("main", Assert.Single(tree.Requests).Ref);
        Assert.All(_store.Items, c =>
        {
            Assert.Equal(CandidateState.Proposed, c.State);
            Assert.False(c.IsAuthoritative);
            Assert.StartsWith("docs-phrases/", c.Evidence.ExtractionMethod, StringComparison.Ordinal);
            Assert.StartsWith("gateway:README.md#L", c.Evidence.SourceLocator, StringComparison.Ordinal);
            Assert.EndsWith("@0123456789ab", c.Evidence.SourceLocator, StringComparison.Ordinal);
        });
        Assert.Contains(_store.Items, c => c.Draft.Kind == RecordKind.Decision);
        Assert.Contains(_store.Items, c => c.Draft.Kind == RecordKind.Standard);
    }

    [Fact]
    public async Task Rescanning_is_idempotent_by_excerpt_hash()
    {
        var scanner = Scanner(new() { ["README.md"] = Readme }, out _);
        await scanner.ScanRepositoryAsync(_admin, "gateway", null, default);

        // Moving the sentence to another file or line does not create a duplicate.
        var moved = Scanner(new() { ["docs/overview.md"] = "\n\n\n" + Readme }, out _);
        var again = await moved.ScanRepositoryAsync(_admin, "gateway", null, default);

        Assert.Equal((0, 4), (again.Created, again.Existing));
        Assert.Equal(4, _store.Items.Count);
    }

    [Fact]
    public async Task Unreadable_and_oversized_files_are_reported_not_fatal()
    {
        var scanner = Scanner(new() { ["docs/a.md"] = null, ["docs/big.md"] = new string('x', DocsScanner.MaxFileBytes + 1), ["docs/ok.md"] = "We decided to keep the monolith for now." }, out _);

        var result = await scanner.ScanRepositoryAsync(_admin, "gateway", null, default);

        Assert.Equal(1, result.Created);
        Assert.Equal(2, result.Skipped.Length);
    }

    [Fact]
    public async Task Scanning_needs_governance_source_rights()
    {
        var scanner = Scanner(new() { ["README.md"] = Readme }, out var tree);
        var reader = new AxiomPrincipal("user:dev", "Dev", Org, [Role.Reader], []);

        await Assert.ThrowsAsync<AxiomException>(() => scanner.ScanRepositoryAsync(reader, "gateway", null, default));
        Assert.Empty(tree.Requests);
    }
}
