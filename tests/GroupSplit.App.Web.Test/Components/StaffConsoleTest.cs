using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Layout;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class StaffConsoleTest : ComponentTest
{
    private readonly Mock<IStaffClient> _staff = new();
    private readonly string _themeMarkup;
    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    public StaffConsoleTest()
    {
        Services.AddSingleton(_staff.Object);
        Services.AddSingleton<ThemePreference>();
        Services.AddMudTheme();
        ComponentFactories.AddStub<AppThemeProvider>();
        Render<MudPopoverProvider>();
        _themeMarkup = Render<MudThemeProvider>(parameters => parameters
            .Add(theme => theme.Theme, Services.GetRequiredService<MudTheme>())).Markup;
    }

    [Fact]
    public void Non_staff_cannot_render_the_staff_pages()
    {
        _staff.Setup(s => s.HasAccessAsync()).ReturnsAsync(false);
        var layout = RenderPage<StaffTickets>();
        layout.WaitForAssertion(() => Assert.Contains("Staff access required", layout.Markup));
        Assert.Empty(layout.FindAll(".staff-panel"));
        _staff.Verify(s => s.TicketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void A_connection_failure_is_distinct_from_access_denied_and_can_be_retried()
    {
        _staff.SetupSequence(s => s.HasAccessAsync()).ThrowsAsync(new HttpRequestException()).ReturnsAsync(false);
        var layout = RenderPage<StaffTickets>();
        layout.WaitForAssertion(() => Assert.Contains("Could not check access", layout.Markup));
        Assert.DoesNotContain("Staff access required", layout.Markup);
        layout.Find(".staff-state button").Click();
        layout.WaitForAssertion(() => Assert.Contains("Staff access required", layout.Markup));
    }

    [Fact]
    public void Ticket_filters_and_pagination_are_reflected_in_record_links()
    {
        var tickets = StaffConsoleFixtures.Tickets();
        _staff.Setup(s => s.TicketsAsync("bank", "New", "High", true, 2))
            .ReturnsAsync(new DirectoryPage<SupportTicketSummary>(tickets.Select(StaffConsoleFixtures.Summary).ToList(), true));
        Navigation.NavigateTo("/staff/tickets?q=bank&status=New&priority=High&review=true&page=2");
        var page = Render<StaffTickets>();
        page.WaitForAssertion(() => Assert.Equal(tickets.Count, page.FindAll("tbody tr").Count));
        var href = page.Find(".staff-record-link").GetAttribute("href")!;
        Assert.Contains("/staff/tickets/", href);
        Assert.Contains("returnTo=", href);
        Assert.Contains("page%3D2", href);
        Assert.Contains("priority%3DHigh", href);
        Assert.Contains("page=3", page.FindAll(".staff-pagination a").Last().GetAttribute("href"));
    }

    [Fact]
    public void Merchants_have_their_own_page_and_do_not_load_tickets()
    {
        var merchants = StaffConsoleFixtures.Merchants();
        _staff.Setup(s => s.MerchantsAsync("", 0)).ReturnsAsync(new DirectoryPage<MerchantResponse>(merchants, false));
        Navigation.NavigateTo("/staff/merchants");
        var page = Render<StaffMerchants>();
        page.WaitForAssertion(() => Assert.Equal(merchants.Count, page.FindAll("tbody tr").Count));
        Assert.Contains("/staff/merchants/new", page.Find(".staff-page-header a").GetAttribute("href"));
        _staff.Verify(s => s.TicketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void A_directory_load_failure_shows_the_real_error_and_recovers_on_retry()
    {
        _staff.SetupSequence(s => s.MerchantsAsync("", 0))
            .ThrowsAsync(new HttpRequestException())
            .ReturnsAsync(new DirectoryPage<MerchantResponse>(StaffConsoleFixtures.Merchants(), false));
        var page = Render<StaffMerchants>();
        page.WaitForAssertion(() => Assert.Contains("Could not load this view", page.Find("[role=alert]").TextContent));
        page.Find(".staff-state button").Click();
        page.WaitForAssertion(() => Assert.Equal(5, page.FindAll("tbody tr").Count));
        Assert.Empty(page.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task Ticket_detail_saves_the_version_token_and_preserves_the_return_destination()
    {
        var ticket = StaffConsoleFixtures.Tickets()[0];
        _staff.Setup(s => s.TicketAsync(ticket.Id)).ReturnsAsync(ticket);
        _staff.Setup(s => s.NotesAsync(ticket.Id)).ReturnsAsync([]);
        _staff.Setup(s => s.SaveTicketAsync(ticket.Id, It.IsAny<UpdateSupportTicketRequest>()))
            .ReturnsAsync(ticket with { Status = "InProgress", ClassificationNeedsReview = false });
        Navigation.NavigateTo($"/staff/tickets/{ticket.Id}?returnTo=%2Fstaff%2Ftickets%3Fstatus%3DNew%26page%3D2");
        var page = Render<StaffTicketDetail>(p => p.Add(c => c.Id, ticket.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".staff-triage")));
        Assert.Equal("/staff/tickets?status=New&page=2", page.Find(".staff-back-link").GetAttribute("href"));
        var status = page.FindComponents<MudSelect<string>>()[0];
        await page.InvokeAsync(() => status.Instance.ValueChanged.InvokeAsync("InProgress"));
        await page.Find(".staff-triage form").SubmitAsync();
        _staff.Verify(s => s.SaveTicketAsync(ticket.Id, It.Is<UpdateSupportTicketRequest>(r =>
            r.Status == "InProgress" && r.ExpectedUpdatedAt == ticket.UpdatedAt)), Times.Once);
        page.WaitForAssertion(() => Assert.Contains("Ticket saved.", page.Markup));
    }

    [Fact]
    public async Task Saving_a_merchant_returns_to_the_filtered_directory_without_discarding_a_saved_draft()
    {
        var merchant = StaffConsoleFixtures.Merchants()[0];
        _staff.Setup(s => s.MerchantAsync(merchant.Id)).ReturnsAsync(merchant);
        _staff.Setup(s => s.SaveMerchantAsync(merchant.Id, It.IsAny<UpdateMerchantRequest>())).Returns(Task.CompletedTask);
        Navigation.NavigateTo($"/staff/merchants/{merchant.Id}?returnTo=%2Fstaff%2Fmerchants%3Fq%3Dcoffee");
        var page = Render<StaffMerchantDetail>(p => p.Add(c => c.Id, merchant.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".staff-merchant-form")));
        await page.Find(".staff-field input").ChangeAsync(new() { Value = "Blue Bottle Coffee Roasters" });
        await page.Find(".staff-merchant-form form").SubmitAsync();
        _staff.Verify(s => s.SaveMerchantAsync(merchant.Id, It.Is<UpdateMerchantRequest>(r => r.Name == "Blue Bottle Coffee Roasters")), Times.Once);
        Assert.EndsWith("/staff/merchants?q=coffee", Navigation.Uri);
        Assert.False(page.FindComponent<MerchantDirectoryEditor>().Instance.HasUnsavedChanges);
    }

    [Fact]
    public async Task Cancelling_a_dirty_merchant_edit_requires_discard_confirmation()
    {
        var editor = Render<MerchantDirectoryEditor>();
        await editor.Find("input").ChangeAsync(new() { Value = "Corner shop" });
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
        Assert.False(await editor.InvokeAsync(() => editor.Instance.CanDiscardAsync()));
        Assert.True(editor.Instance.HasUnsavedChanges);
    }

    [Fact]
    public void Render_all_staff_views_with_sample_data()
    {
        var tickets = StaffConsoleFixtures.Tickets();
        var merchants = StaffConsoleFixtures.Merchants();
        _staff.Setup(s => s.HasAccessAsync()).ReturnsAsync(true);
        _staff.Setup(s => s.TicketsAsync("", "New", "", false, 0)).ReturnsAsync(new DirectoryPage<SupportTicketSummary>(tickets.Select(StaffConsoleFixtures.Summary).ToList(), true));
        _staff.Setup(s => s.MerchantsAsync("", 0)).ReturnsAsync(new DirectoryPage<MerchantResponse>(merchants, false));
        _staff.Setup(s => s.TicketAsync(tickets[0].Id)).ReturnsAsync(tickets[0]);
        _staff.Setup(s => s.MerchantAsync(merchants[0].Id)).ReturnsAsync(merchants[0]);
        var staffId = Guid.NewGuid();
        _staff.Setup(s => s.TeamAsync()).ReturnsAsync([new(staffId, "Alex Morgan", "alex@example.test")]);
        _staff.Setup(s => s.MessagesAsync(tickets[0].Id)).ReturnsAsync([
            new(Guid.NewGuid(), "Reproduced on Android. Checking the latest sync response.", "Alex Morgan", false, "Internal", DateTimeOffset.UtcNow.AddMinutes(-12)),
            new(Guid.NewGuid(), "Thanks for the report. Can you confirm which date the missing payments appeared at your bank?", "Alex Morgan", false, "Public", DateTimeOffset.UtcNow.AddMinutes(-5))]);
        var person = new SupportUserResponse(tickets[0].ReporterUserId, "Taylor Williams", "taylor@example.test", "Active account", 3, 1);
        _staff.Setup(s => s.UserAsync(person.Id)).ReturnsAsync(person);
        _staff.Setup(s => s.UserDirectoryAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync(new DirectoryPage<SupportUserResponse>([person], false));
        _staff.Setup(s => s.AliasesAsync(merchants[0].Id)).ReturnsAsync([new(Guid.NewGuid(), merchants[0].Name, true), new(Guid.NewGuid(), "BLUE BOTTLE #102", false)]);
        List<StaffActivityResponse> activity = [
            new(Guid.NewGuid(), "Tickets", tickets[0].Id, "Status changed", "Alex Morgan", DateTimeOffset.UtcNow.AddMinutes(-8), [new("Status", "New", "InProgress")]),
            new(Guid.NewGuid(), "Merchants", merchants[0].Id, "Merchant updated", "Alex Morgan", DateTimeOffset.UtcNow.AddMinutes(-20), [new("Name", "Blue Bottle", "Blue Bottle Coffee")])];
        _staff.Setup(s => s.ActivityAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<int>()))
            .ReturnsAsync((string? resource, Guid? id, int page) => new DirectoryPage<StaffActivityResponse>(activity.Where(a => (string.IsNullOrEmpty(resource) || a.Resource == resource) && (id is null || a.ResourceId == id)).ToList(), false));

        Capture<StaffTickets>("support-queue", "/staff/tickets");
        Capture<StaffMerchants>("merchant-directory", "/staff/merchants");
        Capture<StaffTicketDetail>("ticket-details", $"/staff/tickets/{tickets[0].Id}", tickets[0].Id);
        Capture<StaffMerchantDetail>("merchant-editor", $"/staff/merchants/{merchants[0].Id}", merchants[0].Id);
        Capture<StaffMerchantDetail>("merchant-new", "/staff/merchants/new");
        Capture<StaffUsers>("users", "/staff/users?q=taylor");
        Capture<StaffUsers>("user-details", $"/staff/users/{person.Id}", person.Id);
        Capture<StaffActivity>("activity", "/staff/activity");
    }

    private void Capture<T>(string name, string uri, Guid? id = null) where T : IComponent
    {
        Navigation.NavigateTo(uri);
        using var layout = RenderPage<T>(id);
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".staff-panel, .staff-ticket-detail, .staff-merchant-detail, .staff-user-card, .staff-activity-feed")));
        Assert.Equal(5, layout.FindAll(".staff-sidebar .staff-nav-link").Count);
        Assert.Empty(layout.FindAll("[role=alert]"));
        StaffConsoleFixtures.Export(_themeMarkup + layout.Markup, name);
    }

    private IRenderedComponent<StaffLayout> RenderPage<T>(Guid? id = null) where T : IComponent =>
        Render<StaffLayout>(parameters => parameters.Add(layout => layout.Body, builder =>
        {
            builder.OpenComponent<T>(0);
            if (id is not null) builder.AddAttribute(1, "Id", id);
            builder.CloseComponent();
        }));
}
