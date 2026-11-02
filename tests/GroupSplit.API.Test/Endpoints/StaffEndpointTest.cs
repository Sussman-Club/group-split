using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Endpoints;

public class StaffEndpointTest : IAsyncLifetime
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

    [Theory]
    [InlineData("GET", "/staff/merchants")]
    [InlineData("POST", "/staff/merchants")]
    [InlineData("POST", "/staff/merchant-images")]
    [InlineData("PUT", "/staff/merchants/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/staff/merchants/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/staff/tickets")]
    [InlineData("PUT", "/staff/tickets/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/staff/tickets/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/staff/tickets/00000000-0000-0000-0000-000000000001/notes")]
    [InlineData("POST", "/staff/tickets/00000000-0000-0000-0000-000000000001/notes")]
    [InlineData("GET", "/staff/team")]
    [InlineData("GET", "/staff/activity")]
    [InlineData("GET", "/staff/users?search=ab")]
    [InlineData("GET", "/staff/views?resource=Tickets")]
    [InlineData("POST", "/staff/views")]
    [InlineData("POST", "/staff/tickets/bulk")]
    [InlineData("GET", "/staff/tickets/00000000-0000-0000-0000-000000000001/messages")]
    [InlineData("POST", "/staff/tickets/00000000-0000-0000-0000-000000000001/reply")]
    [InlineData("GET", "/staff/merchants/00000000-0000-0000-0000-000000000001/aliases")]
    [InlineData("POST", "/staff/merchants/00000000-0000-0000-0000-000000000001/aliases")]
    [InlineData("POST", "/staff/merchants/00000000-0000-0000-0000-000000000001/merge")]
    public async Task Every_staff_operation_requires_staff(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Content = RequestBody(path);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client.SendAsync(request, Ct)).StatusCode);
        using var anonymous = _host.AnonymousClient();
        using var anonymousRequest = new HttpRequestMessage(new HttpMethod(method), path);
        anonymousRequest.Content = RequestBody(path);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(anonymousRequest, Ct)).StatusCode);
    }

    private static HttpContent RequestBody(string path) => path == "/staff/merchant-images"
        ? new MultipartFormDataContent
        {
            { new StringContent("Shop"), "name" },
            { new ByteArrayContent([1]), "file", "logo.png" }
        }
        : JsonContent.Create(new { });

    [Theory]
    [InlineData("{\"roles\":[\"admin\"]}")]
    [InlineData("{\"roles\":[\"default-roles-group-split\"]}")]
    [InlineData("{\"roles\":\"groupsplit-staff\"}")]
    [InlineData("invalid json")]
    public async Task Unrelated_or_malformed_roles_do_not_grant_access(string roles)
    {
        _host.Client.DefaultRequestHeaders.Add("X-Test-Realm-Access", roles);
        Assert.False((await _host.Client.GetFromJsonAsync<StaffAccessResponse>("/staff/access", Ct))!.IsStaff);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client.GetAsync("/staff/merchants", Ct)).StatusCode);
    }

    [Fact]
    public async Task Staff_can_curate_global_merchants_without_expanding_regular_users_visibility()
    {
        using var staff = Staff();
        Assert.True((await staff.GetFromJsonAsync<StaffAccessResponse>("/staff/access", Ct))!.IsStaff);
        var created = await staff.PostAsJsonAsync("/staff/merchants", new CreateMerchantRequest { Name = "Corner shop" }, Ct);
        created.EnsureSuccessStatusCode();
        var merchant = (await created.Content.ReadFromJsonAsync<MerchantResponse>(Ct))!;
        Assert.Equal(merchant.Id, (await staff.GetFromJsonAsync<MerchantResponse>($"/staff/merchants/{merchant.Id}", Ct))!.Id);
        var updated = await staff.PutAsJsonAsync($"/staff/merchants/{merchant.Id}", new UpdateMerchantRequest { Name = "Corner Market" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var directory = await staff.GetFromJsonAsync<DirectoryPage<MerchantResponse>>("/staff/merchants?search=market", Ct);
        Assert.Equal("Corner Market", Assert.Single(directory!.Items).Name);
        Assert.Empty((await _host.Client.GetFromJsonAsync<List<MerchantResponse>>("/merchants", Ct))!);
        var duplicate = await staff.PostAsJsonAsync("/staff/merchants", new CreateMerchantRequest { Name = " corner MARKET " }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var unsafeLogo = await staff.PutAsJsonAsync($"/staff/merchants/{merchant.Id}", new UpdateMerchantRequest { Name = "Corner Market", LogoUrl = "https://evil.test/logo.png" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unsafeLogo.StatusCode);
    }

    [Fact]
    public async Task Triage_records_actor_and_status_history_refuses_stale_edits_and_keeps_notes_private()
    {
        var submitted = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest { Description = "Cannot open my group" }, Ct);
        submitted.EnsureSuccessStatusCode();
        var report = (await submitted.Content.ReadFromJsonAsync<SupportReportResponse>(Ct))!;
        using var staff = Staff();
        var queue = await staff.GetFromJsonAsync<DirectoryPage<SupportTicketSummary>>("/staff/tickets?status=New", Ct);
        var ticket = Assert.Single(queue!.Items);
        Assert.Equal(ticket.Id, (await staff.GetFromJsonAsync<SupportTicketResponse>($"/staff/tickets/{ticket.Id}", Ct))!.Id);
        var edit = new UpdateSupportTicketRequest { Type = "Bug", Priority = "High", Status = "Resolved", AssignToMe = true, ExpectedUpdatedAt = ticket.UpdatedAt };
        var savedResponse = await staff.PutAsJsonAsync($"/staff/tickets/{ticket.Id}", edit, Ct);
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<SupportTicketResponse>(Ct))!;
        Assert.NotNull(saved.AssignedUserId);
        Assert.False(saved.ClassificationNeedsReview);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PutAsJsonAsync($"/staff/tickets/{ticket.Id}", edit, Ct)).StatusCode);
        var note = await staff.PostAsJsonAsync($"/staff/tickets/{ticket.Id}/notes", new CreateSupportNoteRequest("Private investigation"), Ct);
        note.EnsureSuccessStatusCode();
        Assert.Single((await staff.GetFromJsonAsync<List<SupportNoteResponse>>($"/staff/tickets/{ticket.Id}/notes", Ct))!);
        var reporterView = await _host.Client.GetStringAsync("/support-reports", Ct);
        Assert.DoesNotContain("Private investigation", reporterView);
        Assert.Contains("Resolved", reporterView);
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var history = await db.Set<SupportTicketStatusChange>().SingleAsync(h => h.TicketId == report.Id && h.ToStatus == SupportTicketStatus.Resolved, Ct);
        Assert.Equal(saved.AssignedUserId, history.ChangedByUserId);
        Assert.NotNull((await db.Set<SupportTicket>().FindAsync([ticket.Id], Ct))!.ResolvedAt);
    }

    [Fact]
    public async Task Directory_is_paged_and_ticket_filters_are_validated()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < 31; i++) db.Add(new Merchant { Name = $"Shop {i:00}", NormalizedName = $"shop {i:00}", FirstSeenAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        using var staff = Staff();
        var first = (await staff.GetFromJsonAsync<DirectoryPage<MerchantResponse>>("/staff/merchants", Ct))!;
        var last = (await staff.GetFromJsonAsync<DirectoryPage<MerchantResponse>>("/staff/merchants?page=1", Ct))!;
        Assert.Equal(30, first.Items.Count); Assert.True(first.HasMore);
        Assert.Single(last.Items); Assert.False(last.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync("/staff/tickets?status=wrong", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync("/staff/tickets?priority=wrong", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync("/staff/tickets?type=wrong", Ct)).StatusCode);
    }
}
