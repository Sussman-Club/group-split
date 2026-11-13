using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public sealed partial class SupportTicketService
{
    public async Task<DirectoryPage<SupportTicketSummary>> ListAsync(SupportTicketQuery filter, CancellationToken ct)
    {
        if (filter.Page is < 0 or > 10000 || filter.Search?.Length > 200)
        {
            throw new ValidationException(ErrorCodes.InvalidSupportTicket, "Check the ticket filters.");
        }

        var query = db.Set<SupportTicket>().AsNoTracking();
        if (!string.IsNullOrEmpty(filter.Status))
        {
            if (!Parse<SupportTicketStatus>(filter.Status, out var status))
            {
                throw InvalidFields();
            }

            query = query.Where(t => t.Status == status);
        }
        if (!string.IsNullOrEmpty(filter.Priority))
        {
            if (!Parse<SupportTicketPriority>(filter.Priority, out var priority))
            {
                throw InvalidFields();
            }

            query = query.Where(t => t.Priority == priority);
        }
        if (!string.IsNullOrEmpty(filter.Type))
        {
            if (!Parse<SupportTicketType>(filter.Type, out var type))
            {
                throw InvalidFields();
            }

            query = query.Where(t => t.Type == type);
        }
        if (filter.NeedsReview)
        {
            query = query.Where(t => t.ClassificationNeedsReview);
        }

        if (filter.AssignedUserId is { } assignee)
        {
            query = query.Where(t => t.AssignedUserId == assignee);
        }

        if (filter.UnassignedOnly)
        {
            query = query.Where(t => t.AssignedUserId == null);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var needle = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(t => t.Title.ToLower().Contains(needle) || t.Description.ToLower().Contains(needle));
        }
        var ordered = filter.Sort switch
        {
            "updated" => query.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id),
            "oldest" => query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
            "priority" => query.OrderBy(t => t.Priority == SupportTicketPriority.Urgent ? 0 : t.Priority == SupportTicketPriority.High ? 1 : t.Priority == SupportTicketPriority.Normal ? 2 : 3).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id),
            _ => throw InvalidFields()
        };
        var rows = await ordered.Skip(filter.Page * PageSize).Take(PageSize + 1)
            .Select(t => new
            {
                t.Id,
                t.ReporterUserId,
                t.AssignedUserId,
                t.Type,
                t.Status,
                t.Priority,
                t.ClassificationNeedsReview,
                t.Title,
                t.CreatedAt,
                t.UpdatedAt
            }).ToListAsync(ct);
        var names = await Names(rows.SelectMany(t => new[] { t.ReporterUserId, t.AssignedUserId ?? Guid.Empty }), ct);
        return new(rows.Take(PageSize).Select(t => new SupportTicketSummary(t.Id, t.ReporterUserId, t.AssignedUserId,
            t.Type.ToString(), t.Status.ToString(), t.Priority.ToString(), t.ClassificationNeedsReview,
            t.Title, t.CreatedAt, t.UpdatedAt)
        {
            AssignedUserName = t.AssignedUserId is { } user ? names.GetValueOrDefault(user, "Deleted account") : null,
            ReporterName = names.GetValueOrDefault(t.ReporterUserId, "Deleted account")
        }).ToList(), rows.Count > PageSize);
    }

    public async Task BulkUpdateAsync(BulkTicketUpdateRequest request, Guid actorUserId, CancellationToken ct)
    {
        if (request.Tickets is null || request.Tickets.Count is < 1 or > 100 || request.Tickets.Select(t => t.Id).Distinct().Count() != request.Tickets.Count
            || request.Status is null && !request.ChangeAssignment)
        {
            throw InvalidFields();
        }

        if (request.Status is not null && !Parse<SupportTicketStatus>(request.Status, out _))
        {
            throw InvalidFields();
        }

        if (request.ChangeAssignment)
        {
            await team.ValidateAssigneeAsync(request.AssignedUserId, ct);
        }

        var ids = request.Tickets.Select(t => t.Id).ToArray();
        var rows = await db.Set<SupportTicket>().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        if (rows.Count != ids.Length)
        {
            throw new NotFoundException(ErrorCodes.SupportTicketNotFound, "One of the selected tickets no longer exists.");
        }

        var expected = request.Tickets.ToDictionary(t => t.Id, t => t.ExpectedUpdatedAt);
        if (rows.Any(t => t.UpdatedAt != expected[t.Id]))
        {
            throw Changed();
        }

        foreach (var ticket in rows)
        {
            ApplyUpdate(ticket, new UpdateSupportTicketRequest
            {
                Type = ticket.Type.ToString(),
                Priority = ticket.Priority.ToString(),
                Status = request.Status ?? ticket.Status.ToString(),
                ClassificationNeedsReview = ticket.ClassificationNeedsReview,
                ExpectedUpdatedAt = expected[ticket.Id]
            }, actorUserId, request.ChangeAssignment ? request.AssignedUserId : ticket.AssignedUserId);
        }

        await SaveTicketChanges(ct);
    }

    private void ApplyUpdate(SupportTicket ticket, UpdateSupportTicketRequest request, Guid actor, Guid? assignee)
    {
        if (!Parse<SupportTicketType>(request.Type, out var type) || !Parse<SupportTicketStatus>(request.Status, out var status)
            || !Parse<SupportTicketPriority>(request.Priority, out var priority))
        {
            throw InvalidFields();
        }

        if (ticket.UpdatedAt != request.ExpectedUpdatedAt)
        {
            throw Changed();
        }

        var changes = new List<ActivityChange>();
        if (ticket.Type != type)
        {
            changes.Add(new("Type", ticket.Type.ToString(), type.ToString()));
        }

        if (ticket.Status != status)
        {
            changes.Add(new("Status", ticket.Status.ToString(), status.ToString()));
        }

        if (ticket.Priority != priority)
        {
            changes.Add(new("Priority", ticket.Priority.ToString(), priority.ToString()));
        }

        if (ticket.AssignedUserId != assignee)
        {
            changes.Add(new("Assigned to", ticket.AssignedUserId?.ToString(), assignee?.ToString()));
        }

        if (ticket.ClassificationNeedsReview != request.ClassificationNeedsReview)
        {
            changes.Add(new("Needs review", ticket.ClassificationNeedsReview.ToString(), request.ClassificationNeedsReview.ToString()));
        }

        if (changes.Count == 0)
        {
            return;
        }

        var now = NextTime(ticket);
        ChangeStatus(ticket, status, actor, now);
        ticket.Type = type;
        ticket.Priority = priority;
        ticket.AssignedUserId = assignee;
        ticket.ClassificationNeedsReview = request.ClassificationNeedsReview;
        ticket.UpdatedAt = now;
        StaffActivityService.Record(db, actor, "Tickets", ticket.Id, "Ticket updated", now, changes.ToArray());
    }

    private DateTimeOffset NextTime(SupportTicket ticket)
    {
        var instant = clock.GetUtcNow();
        var now = new DateTimeOffset(instant.Ticks / 10 * 10, instant.Offset);
        return now > ticket.UpdatedAt ? now : ticket.UpdatedAt.AddTicks(10);
    }

    private void ChangeStatus(SupportTicket ticket, SupportTicketStatus status, Guid actor, DateTimeOffset at)
    {
        if (ticket.Status == status)
        {
            return;
        }

        db.Add(new SupportTicketStatusChange { TicketId = ticket.Id, ChangedByUserId = actor, FromStatus = ticket.Status, ToStatus = status, ChangedAt = at });
        ticket.Status = status;
        if (status == SupportTicketStatus.Resolved)
        {
            ticket.ResolvedAt = at;
        }
        else if (status != SupportTicketStatus.Closed)
        {
            ticket.ResolvedAt = null;
        }

        ticket.ClosedAt = status == SupportTicketStatus.Closed ? at : null;
    }

    private async Task SaveTicketChanges(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { throw Changed(); }
    }

    private async Task<Dictionary<Guid, string>> Names(IEnumerable<Guid> ids, CancellationToken ct) =>
        await db.Set<User>().AsNoTracking().Where(u => ids.Contains(u.Id) && u.Kind == UserKind.Account)
            .Select(u => new { u.Id, Name = ((u.FirstName ?? "") + " " + (u.LastName ?? "")).Trim() })
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

    private async Task<SupportTicketResponse> DescribeWithNames(SupportTicket ticket, CancellationToken ct)
    {
        var names = await Names([ticket.ReporterUserId, ticket.AssignedUserId ?? Guid.Empty], ct);
        return Describe(ticket) with
        {
            ReporterName = names.GetValueOrDefault(ticket.ReporterUserId, "Deleted account"),
            AssignedUserName = ticket.AssignedUserId is { } id ? names.GetValueOrDefault(id, "Deleted account") : null
        };
    }

    private static ValidationException InvalidFields() => new(ErrorCodes.InvalidSupportTicket, "Check the ticket fields and filters.");
    private static ConflictException Changed() => new(ErrorCodes.SupportTicketChanged, "A ticket changed. Reload before saving.");
}
