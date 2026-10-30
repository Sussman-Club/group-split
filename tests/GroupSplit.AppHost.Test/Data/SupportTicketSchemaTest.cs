using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.AppHost.Test.Data;

public class SupportTicketSchemaTest(AppHostFixture appHost) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await appHost.SeedAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = 120_000)]
    public async Task Ticket_conversation_and_history_round_trip_and_cascade()
    {
        await using var db = await appHost.GetDbContextAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = await db.Set<User>().Select(x => x.Id).FirstAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var ticket = new SupportTicket { ReporterUserId = userId, Type = SupportTicketType.Bug, Title = "Problem", Description = "Details", CreatedAt = now, UpdatedAt = now };
        db.Add(ticket);
        db.Add(new SupportTicketMessage { TicketId = ticket.Id, AuthorUserId = userId, Body = "Reply", CreatedAt = now });
        db.Add(new SupportTicketAttachment { TicketId = ticket.Id, ObjectKey = Guid.NewGuid().ToString(), FileName = "screen.png", ContentType = "image/png", Length = 10, UploadedAt = now });
        db.Add(new SupportTicketStatusChange { TicketId = ticket.Id, ToStatus = SupportTicketStatus.New, ChangedAt = now });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        Assert.Equal(SupportTicketPriority.Normal, (await db.Set<SupportTicket>().SingleAsync(x => x.Id == ticket.Id, ct)).Priority);
        Assert.Single(await db.Set<SupportTicketMessage>().Where(x => x.TicketId == ticket.Id).ToListAsync(ct));
        await db.Set<SupportTicket>().Where(x => x.Id == ticket.Id).ExecuteDeleteAsync(ct);
        Assert.False(await db.Set<SupportTicketMessage>().AnyAsync(x => x.TicketId == ticket.Id, ct));
        Assert.False(await db.Set<SupportTicketAttachment>().AnyAsync(x => x.TicketId == ticket.Id, ct));
        Assert.False(await db.Set<SupportTicketStatusChange>().AnyAsync(x => x.TicketId == ticket.Id, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task Database_rejects_diagnostics_without_consent_and_self_duplicates()
    {
        await using var db = await appHost.GetDbContextAsync();
        var ct = TestContext.Current.CancellationToken;
        var userId = await db.Set<User>().Select(x => x.Id).FirstAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var ticket = new SupportTicket { ReporterUserId = userId, Type = SupportTicketType.Bug, Title = "Problem", Description = "Details", CreatedAt = now, UpdatedAt = now, Platform = "Android" };
        db.Add(ticket);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.Equal("CK_SupportTicket_DiagnosticsConsent", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        db.ChangeTracker.Clear();
        ticket.Platform = null;
        ticket.DuplicateOfTicketId = ticket.Id;
        db.Add(ticket);
        error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.Equal("CK_SupportTicket_Duplicate", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
    }
}
