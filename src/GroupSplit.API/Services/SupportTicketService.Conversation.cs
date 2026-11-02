using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public sealed partial class SupportTicketService
{
    public Task<List<SupportMessageResponse>> StaffMessagesAsync(Guid id, CancellationToken ct) => Messages(id, null, 0, ct);
    public Task<List<SupportMessageResponse>> ReporterMessagesAsync(Guid id, Guid reporterId, CancellationToken ct) => Messages(id, reporterId, 0, ct);

    public Task<List<SupportMessageResponse>> StaffMessagesAsync(Guid id, int page, CancellationToken ct) => Messages(id, null, page, ct);
    public Task<List<SupportMessageResponse>> ReporterMessagesAsync(Guid id, Guid reporterId, int page, CancellationToken ct) => Messages(id, reporterId, page, ct);

    private async Task<List<SupportMessageResponse>> Messages(Guid id, Guid? reporterId, int page, CancellationToken ct)
    {
        if (page is < 0 or > 10000) throw InvalidFields();
        var ticket = await VisibleTicket(id, reporterId, ct);
        var query = db.Set<SupportTicketMessage>().AsNoTracking().Where(m => m.TicketId == id);
        if (reporterId is not null) query = query.Where(m => m.Visibility == SupportMessageVisibility.Public);
        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Skip(page * 100).Take(100).ToListAsync(ct);
        rows.Reverse();
        var names = await Names(rows.Where(m => m.AuthorUserId is not null).Select(m => m.AuthorUserId!.Value), ct);
        return rows.Select(m => new SupportMessageResponse(m.Id, m.Body,
            m.AuthorUserId is { } author ? names.GetValueOrDefault(author, "Deleted account") : "Support team",
            m.AuthorUserId == ticket.ReporterUserId, m.Visibility.ToString(), m.CreatedAt)).ToList();
    }

    public Task<SupportMessageResponse> StaffReplyAsync(Guid id, CreateSupportMessageRequest request, Guid actor, CancellationToken ct) =>
        PostMessage(id, request, actor, true, false, ct);

    public Task<SupportMessageResponse> ReporterReplyAsync(Guid id, CreateSupportMessageRequest request, Guid reporterId, CancellationToken ct) =>
        PostMessage(id, request, reporterId, true, true, ct);

    private async Task<SupportMessageResponse> PostMessage(Guid id, CreateSupportMessageRequest request, Guid actor, bool isPublic, bool fromReporter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 10000) throw InvalidFields();
        var ticket = await VisibleTicket(id, fromReporter ? actor : null, ct);
        if (request.ExpectedUpdatedAt is { } expected && ticket.UpdatedAt != expected) throw Changed();
        var now = NextTime(ticket);
        var message = new SupportTicketMessage { TicketId = id, AuthorUserId = actor, Body = request.Body.Trim(),
            Visibility = isPublic ? SupportMessageVisibility.Public : SupportMessageVisibility.Internal, CreatedAt = now };
        db.Add(message);
        var beforeStatus = ticket.Status;
        if (isPublic && fromReporter)
            ChangeStatus(ticket, ticket.AssignedUserId is null ? SupportTicketStatus.New : SupportTicketStatus.InProgress, actor, now);
        else if (isPublic && request.WaitForCustomer)
            ChangeStatus(ticket, SupportTicketStatus.WaitingForCustomer, actor, now);
        ticket.UpdatedAt = now;
        if (fromReporter)
            await push.ForStaff(PushNotificationKind.StaffCustomerReplied, id, actor, ct);
        StaffActivityService.Record(db, actor, "Tickets", id, fromReporter ? "Customer replied" : isPublic ? "Reply sent to customer" : "Internal note added", now,
            new ActivityChange("Status", beforeStatus.ToString(), ticket.Status.ToString()));
        await SaveTicketChanges(ct);
        var names = await Names([actor], ct);
        return new(message.Id, message.Body, names.GetValueOrDefault(actor, "Support team"), fromReporter, message.Visibility.ToString(), now);
    }

    private async Task<SupportTicket> VisibleTicket(Guid id, Guid? reporterId, CancellationToken ct) =>
        await db.Set<SupportTicket>().SingleOrDefaultAsync(t => t.Id == id && (reporterId == null || t.ReporterUserId == reporterId), ct)
        ?? throw new NotFoundException(ErrorCodes.SupportTicketNotFound, "Ticket not found.");

}
