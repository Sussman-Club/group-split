using GroupSplit.API.Services.Banking.Suggestions;
using Moq;
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

    [Fact]
    public async Task Description_supplies_a_summary_when_no_title_is_given()
    {
        var response = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest
        { Type = "Bug", Description = "  The app\n   stops responding  " }, TestContext.Current.CancellationToken);
        var report = (await response.Content.ReadFromJsonAsync<SupportReportResponse>(TestContext.Current.CancellationToken))!;
        Assert.Equal("The app stops responding", report.Title);
    }

    [Theory]
    [InlineData("FeatureRequest", "High")]
    [InlineData("Bug", "Urgent")]
    [InlineData("Support", "Low")]
    public async Task Description_only_reports_store_classified_type_and_priority(string type, string priority)
    {
        var decisions = new Mock<IDecisionClient>();
        decisions.SetupSequence(x => x.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionAnswer(type, 0.95))
            .ReturnsAsync(new DecisionAnswer(priority, 0.95));
        await using var host = await ApiEndpointHost.StartAsync(services =>
        {
            services.AddSingleton(decisions.Object);
            services.Configure<FilingSuggestionOptions>(o =>
            {
                o.Endpoint = new Uri("https://classifier.example/");
                o.Key = "test";
                o.Deployment = "test";
            });
        });
        var ct = TestContext.Current.CancellationToken;
        var response = await host.Client.PostAsJsonAsync("/support-reports", new { Description = "My feedback" }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<SupportReportResponse>(ct))!;
        Assert.Equal(type, report.Type);
        using var scope = host.Services.CreateScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<SupportTicket>().SingleAsync(x => x.Id == report.Id, ct);
        Assert.Equal(priority, ticket.Priority.ToString());
        Assert.False(ticket.ClassificationNeedsReview);
        decisions.Verify(x => x.ChooseAsync(It.IsAny<object>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Unconfigured_classifier_accepts_description_only_with_safe_defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _host.Client.PostAsJsonAsync("/support-reports", new { Description = "My feedback" }, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<SupportReportResponse>(ct))!;
        Assert.Equal("Support", report.Type);
        using var scope = _host.Services.CreateScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<SupportTicket>().SingleAsync(x => x.Id == report.Id, ct);
        Assert.Equal(SupportTicketPriority.Normal, ticket.Priority);
        Assert.True(ticket.ClassificationNeedsReview);
    }
    [Theory]
    [InlineData("Bogus", "Title", "Details")]
        [InlineData("Bug", "Title", " ")]
    public async Task Invalid_reports_are_rejected(string type, string title, string description)
    {
        var response = await _host.Client.PostAsJsonAsync("/support-reports", new CreateSupportReportRequest
        { Type = type, Title = title, Description = description }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
