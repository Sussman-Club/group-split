using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class HouseholdMonthApi
{
    public static RouteHandlerBuilder MapHouseholdMonth(this RouteGroupBuilder group) =>
        group.MapGet("{id:guid}/household-month", async (Guid id, int year, int month, string timeZone,
            IGroupService groups, ITransactionService transactions, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (year is < 2 or > 9998 || month is < 1 or > 12 || string.IsNullOrWhiteSpace(timeZone))
                return Results.BadRequest("Provide a valid year, month and time zone.");

            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { return Results.BadRequest("The time zone is not available."); }

            var localStart = new DateTime(year, month, 1);
            var localEnd = localStart.AddMonths(1);
            if (zone.IsInvalidTime(localStart) || zone.IsAmbiguousTime(localStart) ||
                zone.IsInvalidTime(localEnd) || zone.IsAmbiguousTime(localEnd))
                return Results.BadRequest("A month boundary is ambiguous or invalid in this time zone.");

            var from = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone));
            var until = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone));
            if (!await (await groups.GetGroupById(id, ct)).AnyAsync(ct))
                return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

            var expenses = (await transactions.InGroup(id, ct)).Where(e => e.DateTime >= from && e.DateTime < until);
            var spending = await expenses.ToCategorySpendingAsync(new GroupSpendingFilter(), currentUser.User.Id, ct);
            var paid = await expenses.Where(e => e.UserId == currentUser.User.Id)
                .GroupBy(e => e.Currency)
                .Select(g => new { Currency = g.Key, Total = g.Sum(e => e.Amount) }).ToListAsync(ct);
            var categories = spending.OtherCurrencies.Select(c => (c.Currency, c.Categories))
                .Prepend((spending.Currency, spending.Categories));
            var exposure = categories.Select(c => new HouseholdCurrencyExposure(c.Currency,
                paid.FirstOrDefault(p => p.Currency == c.Currency)?.Total ?? 0m,
                c.Categories.Sum(row => row.Share)))
                .Where(c => c.Paid != 0m || c.Share != 0m)
                .OrderBy(c => c.Currency, StringComparer.Ordinal).ToArray();
            return Results.Ok(new HouseholdMonthResponse(year, month, zone.Id, from, until, spending, exposure));
        }).WithName("GetHouseholdMonth").Produces<HouseholdMonthResponse>().Produces(StatusCodes.Status400BadRequest);
}
