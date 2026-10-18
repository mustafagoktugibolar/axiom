using System.Collections.Immutable;
using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;

namespace Axiom.UnitTests.Candidates;

internal sealed class InMemoryCandidateStore : ICandidateStore
{
    public List<Candidate> Items { get; } = [];

    public List<IntegrationEvent> Events { get; } = [];

    public Task<Candidate?> FindAsync(string organizationId, string candidateId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(c => c.OrganizationId == organizationId && c.Id == candidateId));

    public Task<CandidateAddResult> AddAsync(Candidate candidate, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        var existing = Items.FirstOrDefault(c => c.OrganizationId == candidate.OrganizationId && c.Id == candidate.Id);
        if (existing is not null)
        {
            return Task.FromResult(new CandidateAddResult(existing, false));
        }

        Items.Add(candidate);
        Events.AddRange(events);
        return Task.FromResult(new CandidateAddResult(candidate, true));
    }

    public Task<bool> UpdateAsync(Candidate updated, CandidateState expected, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        var index = Items.FindIndex(c => c.OrganizationId == updated.OrganizationId && c.Id == updated.Id);
        if (index < 0 || Items[index].State != expected)
        {
            return Task.FromResult(false);
        }

        Items[index] = updated;
        Events.AddRange(events);
        return Task.FromResult(true);
    }

    public Task<(ImmutableArray<Candidate> Items, int Total)> ListAsync(string organizationId, CandidateListQuery query, CancellationToken cancellationToken)
    {
        var rows = Items.Where(c => c.OrganizationId == organizationId
            && (query.State is null || c.State == query.State)
            && (query.Kind is null || c.Draft.Kind == query.Kind)
            && (query.Owner is null || c.Evidence.SuggestedOwner == query.Owner))
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        return Task.FromResult((rows.Skip(query.Skip).Take(query.Take).ToImmutableArray(), rows.Count));
    }
}

internal sealed class FakeRepositoryView(IReadOnlyDictionary<string, string?> files) : IRepositoryView
{
    public IEnumerable<string> Paths => files.Keys.Order(StringComparer.Ordinal);

    public bool Exists(string path) => files.ContainsKey(path);

    public string? Read(string path) => files.GetValueOrDefault(path);
}

internal sealed class FakeTreeSource(string commit, IReadOnlyDictionary<string, string?> files) : IScmTreeSource
{
    public List<ScmTreeRequest> Requests { get; } = [];

    public Task<ScmTree> GetTreeAsync(ScmTreeRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new ScmTree(commit, new FakeRepositoryView(files)));
    }
}
