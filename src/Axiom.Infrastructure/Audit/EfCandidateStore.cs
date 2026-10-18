using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace Axiom.Infrastructure.Audit;

/// <summary>
/// A knowledge-onboarding candidate. Not governance: it is never read by resolution or evaluation, and
/// nothing in the governance projection refers to it. The columns beside <see cref="Document"/> exist for filtering.
/// </summary>
public sealed class CandidateRow
{
    public required string OrganizationId { get; init; }

    public required string Id { get; init; }

    public required string ExcerptHash { get; init; }

    public required string Kind { get; init; }

    public required string SuggestedOwner { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Current <see cref="CandidateState"/>; the optimistic-concurrency token for decisions.</summary>
    public required string State { get; set; }

    public required string Document { get; set; }
}

internal sealed class CandidateRowConfiguration : IEntityTypeConfiguration<CandidateRow>
{
    public void Configure(EntityTypeBuilder<CandidateRow> builder)
    {
        builder.ToTable("candidate");
        builder.HasKey(c => new { c.OrganizationId, c.Id });
        builder.Property(c => c.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(c => c.Id).HasColumnName("id").HasMaxLength(64);
        builder.Property(c => c.ExcerptHash).HasColumnName("excerpt_hash").HasMaxLength(256);
        builder.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(c => c.SuggestedOwner).HasColumnName("suggested_owner").HasMaxLength(256);
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.State).HasColumnName("state").HasMaxLength(32).IsConcurrencyToken();
        builder.Property(c => c.Document).HasColumnName("document").HasColumnType("jsonb");
        builder.HasIndex(c => new { c.OrganizationId, c.State, c.CreatedAt }).HasDatabaseName("ix_candidate_state");
        builder.HasIndex(c => new { c.OrganizationId, c.ExcerptHash }).IsUnique().HasDatabaseName("ux_candidate_excerpt");
    }
}

public sealed class EfCandidateStore(AxiomDbContext db, IEventOutbox outbox) : ICandidateStore
{
    private static readonly JsonSerializerOptions Options = EfEvaluationStore.DocumentOptions;

    public async Task<Candidate?> FindAsync(string organizationId, string candidateId, CancellationToken cancellationToken)
    {
        var row = await db.Set<CandidateRow>().AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == candidateId, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<CandidateAddResult> AddAsync(Candidate candidate, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        if (await FindAsync(candidate.OrganizationId, candidate.Id, cancellationToken) is { } existing)
        {
            return new CandidateAddResult(existing, false);
        }

        db.Set<CandidateRow>().Add(ToRow(candidate));
        foreach (var integrationEvent in events)
        {
            outbox.Enqueue(integrationEvent);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent import captured the same excerpt first; adding is idempotent.
            db.ChangeTracker.Clear();
            return new CandidateAddResult((await FindAsync(candidate.OrganizationId, candidate.Id, cancellationToken))!, false);
        }

        return new CandidateAddResult(candidate, true);
    }

    public async Task<bool> UpdateAsync(Candidate updated, CandidateState expected, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        var row = await db.Set<CandidateRow>().SingleOrDefaultAsync(c => c.OrganizationId == updated.OrganizationId && c.Id == updated.Id, cancellationToken);
        if (row is null || row.State != expected.ToString())
        {
            return false;
        }

        row.State = updated.State.ToString();
        row.Document = JsonSerializer.Serialize(updated, Options);
        foreach (var integrationEvent in events)
        {
            outbox.Enqueue(integrationEvent);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<(ImmutableArray<Candidate> Items, int Total)> ListAsync(string organizationId, CandidateListQuery query, CancellationToken cancellationToken)
    {
        var rows = db.Set<CandidateRow>().AsNoTracking().Where(c => c.OrganizationId == organizationId);
        if (query.State is { } state)
        {
            var name = state.ToString();
            rows = rows.Where(c => c.State == name);
        }

        if (query.Kind is { } kind)
        {
            var name = kind.ToString();
            rows = rows.Where(c => c.Kind == name);
        }

        if (query.Owner is { } owner)
        {
            rows = rows.Where(c => c.SuggestedOwner == owner);
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken);
        return ([.. page.Select(ToDomain)], total);
    }

    private static CandidateRow ToRow(Candidate candidate) => new()
    {
        OrganizationId = candidate.OrganizationId,
        Id = candidate.Id,
        ExcerptHash = candidate.Evidence.ExcerptHash,
        Kind = candidate.Draft.Kind.ToString(),
        SuggestedOwner = candidate.Evidence.SuggestedOwner ?? string.Empty,
        CreatedAt = candidate.CreatedAt,
        State = candidate.State.ToString(),
        Document = JsonSerializer.Serialize(candidate, Options),
    };

    private static Candidate ToDomain(CandidateRow row) =>
        JsonSerializer.Deserialize<Candidate>(row.Document, Options) ?? throw new InvalidOperationException("Stored candidate is empty.");
}
