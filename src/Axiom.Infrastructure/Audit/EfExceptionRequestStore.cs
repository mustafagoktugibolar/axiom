using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Application.Common;
using Axiom.Application.Exceptions;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace Axiom.Infrastructure.Audit;

public sealed class ExceptionRequestRow
{
    public required string OrganizationId { get; init; }

    public required string Id { get; init; }

    public required string Requester { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string Document { get; init; }
}

public sealed class ExceptionDecisionRow
{
    public required string OrganizationId { get; init; }

    public required string RequestId { get; init; }

    public required bool Approved { get; init; }

    public required string Approver { get; init; }

    public required string ApproverIdentity { get; init; }

    public required string Comment { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }
}

internal sealed class ExceptionRequestRowConfiguration : IEntityTypeConfiguration<ExceptionRequestRow>
{
    public void Configure(EntityTypeBuilder<ExceptionRequestRow> builder)
    {
        builder.ToTable("exception_request");
        builder.HasKey(e => new { e.OrganizationId, e.Id });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
        builder.Property(e => e.Requester).HasColumnName("requester").HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.Document).HasColumnName("document").HasColumnType("jsonb");
        builder.HasIndex(e => new { e.OrganizationId, e.Requester, e.CreatedAt }).HasDatabaseName("ix_exception_request_requester");
    }
}

internal sealed class ExceptionDecisionRowConfiguration : IEntityTypeConfiguration<ExceptionDecisionRow>
{
    public void Configure(EntityTypeBuilder<ExceptionDecisionRow> builder)
    {
        builder.ToTable("exception_decision");

        // One decision per request: deciding twice is a key violation, never a silent overwrite.
        builder.HasKey(e => new { e.OrganizationId, e.RequestId });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.RequestId).HasColumnName("request_id").HasMaxLength(64);
        builder.Property(e => e.Approved).HasColumnName("approved");
        builder.Property(e => e.Approver).HasColumnName("approver").HasMaxLength(256);
        builder.Property(e => e.ApproverIdentity).HasColumnName("approver_identity").HasMaxLength(256);
        builder.Property(e => e.Comment).HasColumnName("comment").HasMaxLength(4000);
        builder.Property(e => e.DecidedAt).HasColumnName("decided_at");
    }
}

public sealed class EfExceptionRequestStore(AxiomDbContext db, IEventOutbox outbox) : IExceptionRequestStore
{
    private static readonly JsonSerializerOptions Options = EfEvaluationStore.DocumentOptions;

    public async Task<StoredExceptionRequest?> FindAsync(string organizationId, string requestId, CancellationToken cancellationToken)
    {
        var row = await db.Set<ExceptionRequestRow>().AsNoTracking().SingleOrDefaultAsync(r => r.OrganizationId == organizationId && r.Id == requestId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var decision = await db.Set<ExceptionDecisionRow>().AsNoTracking().SingleOrDefaultAsync(d => d.OrganizationId == organizationId && d.RequestId == requestId, cancellationToken);
        return ToDomain(row, decision);
    }

    public async Task<StoredExceptionRequest> AddAsync(ExceptionRequest request, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        if (await FindAsync(request.OrganizationId, request.Id, cancellationToken) is { } existing)
        {
            return existing;
        }

        db.Set<ExceptionRequestRow>().Add(new ExceptionRequestRow
        {
            OrganizationId = request.OrganizationId,
            Id = request.Id,
            Requester = request.Requester,
            CreatedAt = request.CreatedAt,
            Document = JsonSerializer.Serialize(request, Options),
        });
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
            // A concurrent identical request won the race; the request is idempotent.
            db.ChangeTracker.Clear();
            return (await FindAsync(request.OrganizationId, request.Id, cancellationToken))!;
        }

        return new StoredExceptionRequest(request, null);
    }

    public async Task<bool> DecideAsync(ExceptionDecision decision, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        db.Set<ExceptionDecisionRow>().Add(new ExceptionDecisionRow
        {
            OrganizationId = decision.OrganizationId,
            RequestId = decision.RequestId,
            Approved = decision.Approved,
            Approver = decision.Approver,
            ApproverIdentity = decision.ApproverIdentity,
            Comment = decision.Comment,
            DecidedAt = decision.DecidedAt,
        });
        foreach (var integrationEvent in events)
        {
            outbox.Enqueue(integrationEvent);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<(ImmutableArray<StoredExceptionRequest> Items, int Total)> ListAsync(
        string organizationId, string? requester, ExceptionRequestStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.Set<ExceptionRequestRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId);
        if (requester is not null)
        {
            query = query.Where(r => r.Requester == requester);
        }

        var decisions = db.Set<ExceptionDecisionRow>().AsNoTracking().Where(d => d.OrganizationId == organizationId);
        query = status switch
        {
            ExceptionRequestStatus.Pending => query.Where(r => !decisions.Any(d => d.RequestId == r.Id)),
            ExceptionRequestStatus.Approved => query.Where(r => decisions.Any(d => d.RequestId == r.Id && d.Approved)),
            ExceptionRequestStatus.Rejected => query.Where(r => decisions.Any(d => d.RequestId == r.Id && !d.Approved)),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        var found = await decisions.Where(d => ids.Contains(d.RequestId)).ToDictionaryAsync(d => d.RequestId, cancellationToken);
        return ([.. rows.Select(r => ToDomain(r, found.GetValueOrDefault(r.Id)))], total);
    }

    private static StoredExceptionRequest ToDomain(ExceptionRequestRow row, ExceptionDecisionRow? decision) => new(
        JsonSerializer.Deserialize<ExceptionRequest>(row.Document, Options) ?? throw new InvalidOperationException("Stored exception request is empty."),
        decision is null ? null : new ExceptionDecision(decision.RequestId, decision.OrganizationId, decision.Approved, decision.Approver, decision.ApproverIdentity, decision.Comment, decision.DecidedAt));
}
