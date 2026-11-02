using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>Ticket operations; callers establish authorization before invoking this service.</summary>
public sealed partial class SupportTicketService(AppDbContext db, TimeProvider clock, SupportReportClassifier classifier, StaffTeamService team)
{
    private const int PageSize = 30;

    public async Task<SupportTicketResponse> GetAsync(Guid id, CancellationToken ct)
    {
        var ticket = await db.Set<SupportTicket>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.SupportTicketNotFound, "Ticket not found.");
        return await DescribeWithNames(ticket, ct);
    }

    public async Task<List<SupportReportResponse>> ListForReporterAsync(Guid reporterUserId, CancellationToken ct)
    {
        var tickets = await db.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.ReporterUserId == reporterUserId)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(100).ToListAsync(ct);
        var ids = tickets.Select(t => t.Id).ToArray();
        var unread = await db.Set<SupportNotification>().Where(n => n.UserId == reporterUserId && n.ReadAt == null && ids.Contains(n.TicketId))
            .Select(n => n.TicketId).Distinct().ToListAsync(ct);
        return tickets.Select(t => new SupportReportResponse(t.Id, t.Type.ToString(), t.Status.ToString(),
            t.Title, t.Description, t.ReproductionSteps, t.CreatedAt) { HasUnreadReplies = unread.Contains(t.Id) }).ToList();
    }

    public async Task<SupportReportResponse> CreateAsync(CreateSupportReportRequest request, Guid reporterUserId, CancellationToken ct)
    {
        if ((request.Type is not null && (!Enum.TryParse<SupportTicketType>(request.Type, false, out var type)
            || !Enum.IsDefined(type))) || request.Title?.Length > 200 || string.IsNullOrWhiteSpace(request.Description)
            || request.Description.Length > 10000 || request.ReproductionSteps?.Length > 10000)
            throw new ValidationException(ErrorCodes.InvalidSupportReport, "Check the report description and fields.");
        var description = request.Description.Trim();
        var summary = string.Join(" ", description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (summary.Length > 200)
        {
            var end = char.IsHighSurrogate(summary[198]) ? 198 : 199;
            summary = summary[..end].TrimEnd() + "…";
        }
        var title = string.IsNullOrWhiteSpace(request.Title) ? summary : request.Title.Trim();
        var classification = await classifier.ClassifyAsync(description, ct);
        var now = clock.GetUtcNow();
        var ticket = new SupportTicket { ReporterUserId = reporterUserId, Type = classification.Type, Priority = classification.Priority, ClassificationNeedsReview = classification.NeedsReview,
            Title = title, Description = description,
            ReproductionSteps = string.IsNullOrWhiteSpace(request.ReproductionSteps) ? null : request.ReproductionSteps.Trim(),
            CreatedAt = now, UpdatedAt = now };
        db.Add(ticket);
        db.Add(new SupportTicketStatusChange { TicketId = ticket.Id, ChangedByUserId = reporterUserId,
            ToStatus = SupportTicketStatus.New, ChangedAt = now });
        StaffActivityService.Record(db, reporterUserId, "Tickets", ticket.Id, "Report submitted", now);
        await db.SaveChangesAsync(ct);
        return new SupportReportResponse(ticket.Id, ticket.Type.ToString(), ticket.Status.ToString(),
            ticket.Title, ticket.Description, ticket.ReproductionSteps, ticket.CreatedAt);
    }


    public Task<DirectoryPage<SupportTicketResponse>> ListAsync(string? search, string? status, string? priority, bool? needsReview, int? page, CancellationToken ct) =>
        ListAsync(new SupportTicketQuery { Search = search, Status = status, Priority = priority, NeedsReview = needsReview == true, Page = page ?? 0 }, ct);

    public async Task<SupportTicketResponse> UpdateAsync(Guid id, UpdateSupportTicketRequest request, Guid actorUserId, CancellationToken ct)
    {
        var ticket = await db.Set<SupportTicket>().FindAsync([id], ct)
            ?? throw new NotFoundException(ErrorCodes.SupportTicketNotFound, "Ticket not found.");
        var assignee = request.AssignToMe ? actorUserId : request.ChangeAssignment ? request.AssignedUserId : ticket.AssignedUserId;
        if (assignee != ticket.AssignedUserId) await team.ValidateAssigneeAsync(assignee, ct);
        ApplyUpdate(ticket, request, actorUserId, assignee);
        await SaveTicketChanges(ct);
        return await DescribeWithNames(ticket, ct);
    }

    public async Task<List<SupportNoteResponse>> ListNotesAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Set<SupportTicket>().AnyAsync(t => t.Id == id, ct)) throw new NotFoundException(ErrorCodes.SupportTicketNotFound, "Ticket not found.");
        var notes = await db.Set<SupportTicketMessage>().AsNoTracking()
            .Where(m => m.TicketId == id && m.Visibility == SupportMessageVisibility.Internal)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(100)
            .Select(m => new SupportNoteResponse(m.Id, m.AuthorUserId, m.Body, m.CreatedAt)).ToListAsync(ct);
        return notes;
    }

    public async Task<SupportNoteResponse> AddNoteAsync(Guid id, CreateSupportNoteRequest request, Guid actorUserId, CancellationToken ct)
    {
        var message = await PostMessage(id, new CreateSupportMessageRequest { Body = request.Body }, actorUserId, false, false, ct);
        return new(message.Id, actorUserId, message.Body, message.CreatedAt);
    }

    private static bool Parse<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, false, out parsed) && Enum.IsDefined(parsed);
    private static SupportTicketResponse Describe(SupportTicket t) => new(t.Id, t.ReporterUserId, t.AssignedUserId,
        t.Type.ToString(), t.Status.ToString(), t.Priority.ToString(), t.ClassificationNeedsReview,
        t.Title, t.Description, t.ReproductionSteps, t.CreatedAt, t.UpdatedAt);
}
