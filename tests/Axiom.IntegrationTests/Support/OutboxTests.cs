using Axiom.Application.Common;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.IntegrationTests.Support;

[Collection(PostgresTests.Name)]
public class OutboxTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Enqueuing_the_same_event_twice_stores_it_once()
    {
        var organization = PostgresFixture.NewOrganization();
        var occurredAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var first = IntegrationEvent.Create(EventTypes.GovernanceSnapshotPublished, organization, occurredAt, "gs_1", new { snapshotId = "gs_1" });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = postgres.CreateContext();
            var outbox = new EfEventOutbox(db);
            outbox.Enqueue(first);
            outbox.Enqueue(first);
            await db.SaveChangesAsync();
        }

        await using var verify = postgres.CreateContext();
        var stored = await verify.Set<OutboxEvent>().SingleAsync(e => e.OrganizationId == organization);
        Assert.Equal(first.EventId, stored.EventId);
        Assert.Equal("{\"snapshotId\": \"gs_1\"}", stored.Data);
        Assert.Null(stored.DispatchedAt);
    }
}
