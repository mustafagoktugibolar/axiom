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

/// <summary>A repository to read at the head of a branch. A null <see cref="Ref"/> means the default branch.</summary>
public sealed record ScmTreeRequest(string OrganizationId, string Repository, string? Ref);

/// <summary>The repository tree at one immutable commit.</summary>
public sealed record ScmTree(string CommitSha, IRepositoryView Tree);

/// <summary>
/// Reads the files of a registered repository at a branch head (knowledge onboarding). It uses the same
/// adapter, allowed hosts and credentials as <see cref="IScmDiffSource"/>; it only reads.
/// </summary>
public interface IScmTreeSource
{
    Task<ScmTree> GetTreeAsync(ScmTreeRequest request, CancellationToken cancellationToken);
}
