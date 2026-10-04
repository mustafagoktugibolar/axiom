using System.Collections.Immutable;
using Axiom.Domain.Policy;

namespace Axiom.Application.Evaluation;

public sealed record ScmDiffRequest(string OrganizationId, string Repository, string BaseSha, string HeadSha);

/// <summary>
/// The change between two immutable commits. <see cref="Tree"/> is the repository at the head commit.
/// When the diff was larger than the adapter's limits, <see cref="Truncated"/> is set and only the
/// first files in path order are present: the evaluator must not treat a truncated diff as complete.
/// </summary>
public sealed record ScmDiff(ImmutableArray<ChangedFile> Files, IRepositoryView Tree, bool Truncated, int TotalFiles);

/// <summary>
/// Fetches diffs by commit SHA from the source-control system (design.md §4.7 diff validator). It only
/// reads, and it addresses commits, never working trees, so the result is independent of the harness.
/// </summary>
public interface IScmDiffSource
{
    Task<ScmDiff> GetDiffAsync(ScmDiffRequest request, CancellationToken cancellationToken);
}
