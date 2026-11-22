using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// Repeating expenses over HTTP (#546): one asked for with the expense itself, and the group's
/// list, pause and stop.
/// </summary>
public class RecurringExpenseEndpointTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_expense_saved_with_repeat_is_listed_from_the_next_month_and_can_be_paused_and_stopped()
    {
        await using var host = await ApiEndpointHost.StartAsync();
        var group = await Read<GroupResponse>(await host.Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Home" }, Json, Ct));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // On the wire by name, as an agent or the app sends it.
        var created = await host.Client.PostAsJsonAsync("/transactions", new
        {
            groupId = group.Id,
            name = "Rent",
            amount = 900,
            dateTime = DateTimeOffset.UtcNow,
            repeat = new { frequency = "Monthly", from = today }
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var listed = Assert.Single(await host.Client.GetFromJsonAsync<List<RecurringExpenseResponse>>(
            $"/recurring-expenses?groupId={group.Id}", Json, Ct) ?? []);
        Assert.Equal("Rent", listed.Details.Name);
        Assert.Equal(RepeatFrequency.Monthly, listed.Details.Frequency);
        Assert.Equal(today.AddMonths(1), listed.NextOn);

        var paused = await Read<RecurringExpenseResponse>(
            await host.Client.PostAsync($"/recurring-expenses/{listed.Id}/pause", null, Ct));
        Assert.True(paused.Paused);

        Assert.Equal(HttpStatusCode.NoContent,
            (await host.Client.DeleteAsync($"/recurring-expenses/{listed.Id}", Ct)).StatusCode);
        Assert.Empty(await host.Client.GetFromJsonAsync<List<RecurringExpenseResponse>>(
            $"/recurring-expenses?groupId={group.Id}", Json, Ct) ?? []);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }
}
