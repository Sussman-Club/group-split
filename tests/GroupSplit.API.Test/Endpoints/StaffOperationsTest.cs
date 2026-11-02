using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Endpoints;

public class StaffOperationsTest : IAsyncLifetime
{
    private ApiEndpointHost _host = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();
    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
    private HttpClient Staff()
    {
        var client = _host.ClientForAnotherUser();
        client.DefaultRequestHeaders.Add("X-Test-Realm-Access", "{\"roles\":[\"groupsplit-staff\"]}");
        return client;
    }
    private async Task<SupportReportResponse> Report(string description = "Please help with an invitation")
    {
        var response = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest { Description = description }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SupportReportResponse>(Ct))!;
    }
    private static async Task<MerchantResponse> Merchant(HttpClient staff, string name)
    {
        var response = await staff.PostAsJsonAsync("/staff/merchants", new CreateMerchantRequest { Name = name }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MerchantResponse>(Ct))!;
    }

    [Fact]
    public async Task Public_replies_notify_the_reporter_but_notes_and_other_users_tickets_stay_private()
    {
        var report = await Report();
        using var staff = Staff();
        (await staff.PostAsJsonAsync($"/staff/tickets/{report.Id}/notes", new CreateSupportNoteRequest("Private investigation"), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(0, await _host.Client.GetFromJsonAsync<int>("/support-reports/notifications", Ct));
        var ticket = (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{report.Id}", Ct))!;
        var reply = new CreateSupportMessageRequest { Body = "Please try the new invitation link.", ExpectedUpdatedAt = ticket.UpdatedAt };
        (await staff.PostAsJsonAsync($"/staff/tickets/{report.Id}/reply", reply, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/staff/tickets/{report.Id}/reply", reply, Ct)).StatusCode);
        Assert.Equal("WaitingForCustomer", (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{report.Id}", Ct))!.Status);
        Assert.Equal(1, await _host.Client.GetFromJsonAsync<int>("/support-reports/notifications", Ct));
        Assert.True(Assert.Single((await _host.Client.GetFromJsonAsync<List<SupportReportResponse>>("/support-reports", Ct))!).HasUnreadReplies);
        var messages = (await _host.Client.GetFromJsonAsync<List<SupportMessageResponse>>($"/support-reports/{report.Id}/messages", Ct))!;
        Assert.Equal(reply.Body, Assert.Single(messages).Body);
        Assert.Equal("Public", messages[0].Visibility);
        using var other = _host.ClientForAnotherUser();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/support-reports/{report.Id}/messages", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/support-reports/{report.Id}/messages", new CreateSupportMessageRequest { Body = "intrusion" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/support-reports/{report.Id}/read", null, Ct)).StatusCode);
        (await _host.Client.PostAsJsonAsync($"/support-reports/{report.Id}/messages", new CreateSupportMessageRequest { Body = "That worked, thanks" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(0, await _host.Client.GetFromJsonAsync<int>("/support-reports/notifications", Ct));
        Assert.Equal(3, (await staff.GetFromJsonAsync<List<SupportMessageResponse>>($"/staff/tickets/{report.Id}/messages", Ct))!.Count);
        Assert.Equal("New", (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{report.Id}", Ct))!.Status);
        var audit = (await staff.GetFromJsonAsync<DirectoryPage<StaffActivityResponse>>($"/staff/activity?resource=Tickets&resourceId={report.Id}", Ct))!;
        Assert.Contains(audit.Items, a => a.Action == "Reply sent to customer");
        Assert.DoesNotContain("Private investigation", System.Text.Json.JsonSerializer.Serialize(audit));
    }

    [Fact]
    public async Task Older_conversation_pages_remain_owner_only_and_exclude_every_internal_note()
    {
        var report = await Report();
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reporterId = (await db.Set<SupportTicket>().FindAsync([report.Id], Ct))!.ReporterUserId;
            for (var i = 0; i < 101; i++) db.Add(new SupportTicketMessage { TicketId = report.Id, AuthorUserId = reporterId, Body = $"Public {i}", Visibility = SupportMessageVisibility.Public, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i) });
            db.Add(new SupportTicketMessage { TicketId = report.Id, Body = "Private", Visibility = SupportMessageVisibility.Internal, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }
        var latest = (await _host.Client.GetFromJsonAsync<List<SupportMessageResponse>>($"/support-reports/{report.Id}/messages", Ct))!;
        var older = (await _host.Client.GetFromJsonAsync<List<SupportMessageResponse>>($"/support-reports/{report.Id}/messages?page=1", Ct))!;
        Assert.Equal(100, latest.Count);
        Assert.Equal("Public 100", Assert.Single(older).Body);
        Assert.All(latest.Concat(older), message => Assert.Equal("Public", message.Visibility));
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Client.GetAsync($"/support-reports/{report.Id}/messages?page=-1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Assignment_uses_verified_staff_and_bulk_edits_refuse_stale_versions_atomically()
    {
        var first = await Report(); var second = await Report("Another invitation issue");
        using var staff = Staff(); using var colleague = Staff();
        (await staff.GetAsync("/staff/access", Ct)).EnsureSuccessStatusCode();
        (await colleague.GetAsync("/staff/access", Ct)).EnsureSuccessStatusCode();
        var team = (await staff.GetFromJsonAsync<List<StaffPersonResponse>>("/staff/team", Ct))!;
        Assert.Equal(2, team.Count);
        var ticket = (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{first.Id}", Ct))!;
        var edit = new UpdateSupportTicketRequest { Status = ticket.Status, Type = ticket.Type, Priority = ticket.Priority, ChangeAssignment = true, AssignedUserId = Guid.NewGuid(), ExpectedUpdatedAt = ticket.UpdatedAt };
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync($"/staff/tickets/{first.Id}", edit, Ct)).StatusCode);
        edit.AssignedUserId = team[0].Id;
        (await staff.PutAsJsonAsync($"/staff/tickets/{first.Id}", edit, Ct)).EnsureSuccessStatusCode();
        var assigned = (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{first.Id}", Ct))!;
        Assert.NotNull(assigned.AssignedUserName);
        Assert.Single((await staff.GetFromJsonAsync<DirectoryPage<SupportTicketResponse>>($"/staff/tickets?assignee={team[0].Id}", Ct))!.Items);
        var secondTicket = (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{second.Id}", Ct))!;
        var bulk = new BulkTicketUpdateRequest { Status = "Resolved", Tickets = [new(first.Id, ticket.UpdatedAt), new(second.Id, secondTicket.UpdatedAt)] };
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync("/staff/tickets/bulk", bulk, Ct)).StatusCode);
        Assert.Equal("New", (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{second.Id}", Ct))!.Status);
        bulk.Tickets[0] = new(first.Id, assigned.UpdatedAt);
        (await staff.PostAsJsonAsync("/staff/tickets/bulk", bulk, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("Resolved", (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{second.Id}", Ct))!.Status);
    }

    [Fact]
    public async Task Saved_views_are_private_and_user_lookup_returns_only_bounded_support_context()
    {
        var report = await Report();
        using var staff = Staff(); using var colleague = Staff();
        var request = new SaveStaffViewRequest("My urgent queue", "Tickets", "/staff/tickets?assignee=me&priority=Urgent");
        (await staff.PostAsJsonAsync("/staff/views", request, Ct)).EnsureSuccessStatusCode();
        var view = Assert.Single((await staff.GetFromJsonAsync<List<SavedStaffViewResponse>>("/staff/views?resource=Tickets", Ct))!);
        Assert.Empty((await colleague.GetFromJsonAsync<List<SavedStaffViewResponse>>("/staff/views?resource=Tickets", Ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.DeleteAsync($"/staff/views/{view.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/staff/views", request with { Url = "https://external.test" }, Ct)).StatusCode);
        var ticket = (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{report.Id}", Ct))!;
        var userResponse = await staff.GetAsync($"/staff/users/{ticket.ReporterUserId}", Ct);
        userResponse.EnsureSuccessStatusCode();
        var user = (await userResponse.Content.ReadFromJsonAsync<SupportUserResponse>(Ct))!;
        Assert.Equal(1, user.OpenTickets);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync("/staff/users?search=a", Ct)).StatusCode);
        var json = System.Text.Json.JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(Ct));
        Assert.Equal(new[] { "email", "groups", "id", "name", "openTickets", "status" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var audit = (await staff.GetFromJsonAsync<DirectoryPage<StaffActivityResponse>>($"/staff/activity?resource=Users&resourceId={user.Id}", Ct))!;
        Assert.Empty(audit.Items);
    }

    [Fact]
    public async Task Staff_activity_expires_after_seven_days_without_deleting_ticket_history()
    {
        var report = await Report();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var old = new StaffActivity { Resource = "Tickets", ResourceId = report.Id, Action = "Old event", ChangesJson = "[]", At = now.AddDays(-8) };
        var recent = new StaffActivity { Resource = "Tickets", ResourceId = report.Id, Action = "Recent event", ChangesJson = "[]", At = now.AddDays(-6) };
        var view = new StaffActivity { Resource = "Users", ResourceId = Guid.NewGuid(), Action = "Account viewed", ChangesJson = "[]", At = now };
        db.AddRange(old, recent, view); await db.SaveChangesAsync(Ct);
        var service = scope.ServiceProvider.GetRequiredService<GroupSplit.API.Services.StaffActivityService>();
        var visible = await service.ListAsync(null, null, 0, Ct);
        Assert.DoesNotContain(visible.Items, a => a.Action == "Account viewed");
        var retention = scope.ServiceProvider.GetRequiredService<GroupSplit.API.Services.StaffActivityRetention>();
        Assert.Equal(1, await retention.PruneAsync(Ct));
        Assert.False(await db.Set<StaffActivity>().AnyAsync(a => a.Id == old.Id, Ct));
        Assert.True(await db.Set<StaffActivity>().AnyAsync(a => a.Id == recent.Id, Ct));
        Assert.True(await db.Set<SupportTicketStatusChange>().AnyAsync(a => a.TicketId == report.Id, Ct));
    }

    [Fact]
    public async Task Ticket_queue_filters_by_type()
    {
        var bug = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest { Description = "A bug", Type = "Bug" }, Ct);
        bug.EnsureSuccessStatusCode();
        var suggestion = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest { Description = "A suggestion", Type = "FeatureRequest" }, Ct);
        suggestion.EnsureSuccessStatusCode();
        var bugReport = (await bug.Content.ReadFromJsonAsync<SupportReportResponse>(Ct))!;
        var suggestionReport = (await suggestion.Content.ReadFromJsonAsync<SupportReportResponse>(Ct))!;
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Set<SupportTicket>().FindAsync([bugReport.Id], Ct))!.Type = SupportTicketType.Bug;
            (await db.Set<SupportTicket>().FindAsync([suggestionReport.Id], Ct))!.Type = SupportTicketType.FeatureRequest;
            await db.SaveChangesAsync(Ct);
        }
        using var staff = Staff();
        var results = (await staff.GetFromJsonAsync<DirectoryPage<SupportTicketResponse>>("/staff/tickets?type=Bug", Ct))!;
        Assert.Equal("Bug", Assert.Single(results.Items).Type);
    }

    [Fact]
    public async Task Staff_can_page_all_active_accounts_and_filter_the_directory()
    {
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 35; i++) db.Add(new GroupSplit.Data.Entities.User { FirstName = $"Directory {i:00}", Email = $"directory{i:00}@example.test", Kind = UserKind.Account });
            db.Add(new GroupSplit.Data.Entities.User { FirstName = "Former account", Kind = UserKind.Deleted });
            await db.SaveChangesAsync(Ct);
        }
        using var staff = Staff();
        var first = (await staff.GetFromJsonAsync<DirectoryPage<SupportUserResponse>>("/staff/users/directory", Ct))!;
        var second = (await staff.GetFromJsonAsync<DirectoryPage<SupportUserResponse>>("/staff/users/directory?page=1", Ct))!;
        Assert.Equal(30, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotEmpty(second.Items);
        Assert.Empty(first.Items.Select(u => u.Id).Intersect(second.Items.Select(u => u.Id)));
        Assert.DoesNotContain(first.Items.Concat(second.Items), u => u.Name == "Former account");
        var filtered = (await staff.GetFromJsonAsync<DirectoryPage<SupportUserResponse>>("/staff/users/directory?search=directory17", Ct))!;
        Assert.Equal("directory17@example.test", Assert.Single(filtered.Items).Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client.GetAsync("/staff/users/directory", Ct)).StatusCode);
    }

    [Fact]
    public async Task Merchant_aliases_survive_rename_and_merge_with_a_stale_preview_refused()
    {
        using var staff = Staff();
        var source = await Merchant(staff, "Corner Store"); var target = await Merchant(staff, "Corner Market");
        (await staff.PostAsJsonAsync($"/staff/merchants/{source.Id}/aliases", new CreateMerchantAliasRequest("CORNER #123"), Ct)).EnsureSuccessStatusCode();
        var aliases = (await staff.GetFromJsonAsync<List<MerchantAliasResponse>>($"/staff/merchants/{source.Id}/aliases", Ct))!;
        Assert.Equal(2, aliases.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.DeleteAsync($"/staff/merchants/{source.Id}/aliases/{aliases.Single(a => a.IsCanonical).Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/staff/merchants/{target.Id}/aliases", new CreateMerchantAliasRequest("corner store"), Ct)).StatusCode);
        Assert.Single((await staff.GetFromJsonAsync<DirectoryPage<MerchantResponse>>("/staff/merchants?search=%23123", Ct))!.Items);
        var preview = (await staff.GetFromJsonAsync<MerchantMergePreview>($"/staff/merchants/{source.Id}/merge-preview?targetId={target.Id}", Ct))!;
        (await staff.PutAsJsonAsync($"/staff/merchants/{target.Id}", new UpdateMerchantRequest { Name = "Corner Supermarket", ExpectedVersion = target.Version }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/staff/merchants/{source.Id}/merge", new MergeMerchantsRequest(target.Id, preview.Source.Version, preview.Target.Version, preview.Transactions, preview.BankRows, preview.Aliases), Ct)).StatusCode);
        preview = (await staff.GetFromJsonAsync<MerchantMergePreview>($"/staff/merchants/{source.Id}/merge-preview?targetId={target.Id}", Ct))!;
        var wrongImpact = new MergeMerchantsRequest(target.Id, preview.Source.Version, preview.Target.Version, preview.Transactions + 1, preview.BankRows, preview.Aliases);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/staff/merchants/{source.Id}/merge", wrongImpact, Ct)).StatusCode);
        (await staff.PostAsJsonAsync($"/staff/merchants/{source.Id}/merge", new MergeMerchantsRequest(target.Id, preview.Source.Version, preview.Target.Version, preview.Transactions, preview.BankRows, preview.Aliases), Ct)).EnsureSuccessStatusCode();
        Assert.Equal(target.Id, (await staff.GetFromJsonAsync<MerchantResponse>($"/staff/merchants/{source.Id}", Ct))!.Id);
        Assert.Equal(4, (await staff.GetFromJsonAsync<List<MerchantAliasResponse>>($"/staff/merchants/{target.Id}/aliases", Ct))!.Count);
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await db.Set<Merchant>().FindAsync([source.Id], Ct));
        Assert.Equal(target.Id, (await db.Set<MerchantRedirect>().FindAsync([source.Id], Ct))!.MerchantId);
    }
}
