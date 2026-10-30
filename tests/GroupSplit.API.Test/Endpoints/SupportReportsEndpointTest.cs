using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Endpoints;

public class SupportReportsEndpointTest : IAsyncLifetime
{
    private ApiEndpointHost _host = null!;
    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();
    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Reports_are_private_and_record_initial_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest
        { Type = "Bug", Title = "Problem", Description = "Details", ReproductionSteps = "Open the page" }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<SupportReportResponse>(ct))!;
        Assert.Equal("New", created.Status);
        var mine = await _host.Client.GetFromJsonAsync<List<SupportReportResponse>>("/support-reports", ct);
        Assert.Single(mine!);
        using var other = _host.ClientForAnotherUser();
        Assert.Empty((await other.GetFromJsonAsync<List<SupportReportResponse>>("/support-reports", ct))!);
        using var anonymous = _host.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/support-reports", ct)).StatusCode);
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var history = await db.Set<SupportTicketStatusChange>().SingleAsync(x => x.TicketId == created.Id, ct);
        Assert.Equal(SupportTicketStatus.New, history.ToStatus);
    }

    [Theory]
    [InlineData("Bogus", "Title", "Details")]
    [InlineData("Bug", " ", "Details")]
    [InlineData("Bug", "Title", " ")]
    public async Task Invalid_reports_are_rejected(string type, string title, string description)
    {
        var response = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest
        { Type = type, Title = title, Description = description }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
