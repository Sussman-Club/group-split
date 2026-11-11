using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class StaffOperationsUiTest : ComponentTest
{
    private readonly Mock<IStaffClient> _staff = new();
    private readonly Mock<ISupportReportsClient> _reports = new();
    public StaffOperationsUiTest()
    {
        Services.AddSingleton(_staff.Object);
        Services.AddSingleton(_reports.Object);
    }

    [Fact]
    public async Task Staff_composer_defaults_to_internal_and_public_reply_requires_explicit_mode_selection()
    {
        var id = Guid.NewGuid();
        var version = DateTimeOffset.UtcNow;
        _staff.Setup(s => s.MessagesAsync(id)).ReturnsAsync([]);
        var composer = Render<SupportConversation>(p => p.Add(c => c.TicketId, id).Add(c => c.ExpectedUpdatedAt, version));
        Assert.Contains("Only staff can see this note", composer.Markup);
        await composer.Find("textarea").ChangeAsync(new()
        {
            Value = "Investigating privately"
        });
        await composer.Find("form").SubmitAsync();
        _staff.Verify(s => s.AddNoteAsync(id, "Investigating privately"), Times.Once);
        _staff.Verify(s => s.ReplyAsync(It.IsAny<Guid>(), It.IsAny<CreateSupportMessageRequest>()), Times.Never);
        await composer.FindAll(".staff-compose-modes button")[1].ClickAsync();
        Assert.Contains("The reporter can see this reply", composer.Markup);
        await composer.Find("textarea").ChangeAsync(new()
        {
            Value = "Please try again"
        });
        await composer.Find("form").SubmitAsync();
        _staff.Verify(s => s.ReplyAsync(id, It.Is<CreateSupportMessageRequest>(r => r.Body == "Please try again" && r.ExpectedUpdatedAt == version && r.WaitForCustomer)), Times.Once);
    }

    [Fact]
    public async Task Failed_public_reply_keeps_the_draft_and_reporter_composer_has_no_internal_mode()
    {
        var id = Guid.NewGuid();
        _reports.Setup(s => s.MessagesAsync(id)).ReturnsAsync([]);
        _reports.Setup(s => s.ReplyAsync(id, It.IsAny<CreateSupportMessageRequest>())).ThrowsAsync(new HttpRequestException());
        var composer = Render<SupportConversation>(p => p.Add(c => c.TicketId, id).Add(c => c.ForReporter, true));
        Assert.Empty(composer.FindAll(".staff-compose-modes"));
        await composer.Find("textarea").ChangeAsync(new()
        {
            Value = "My reply"
        });
        await composer.Find("form").SubmitAsync();
        Assert.True(composer.Instance.HasDraft);
        Assert.Contains("Your draft is still here", composer.Markup);
        _staff.Verify(s => s.AddNoteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Merge_cannot_execute_until_the_destination_is_previewed_and_confirmation_checked()
    {
        var source = StaffConsoleFixtures.Merchants()[0];
        var target = StaffConsoleFixtures.Merchants()[1];
        _staff.Setup(s => s.AliasesAsync(source.Id)).ReturnsAsync([]);
        _staff.Setup(s => s.MerchantsAsync("Corner", 0)).ReturnsAsync(new DirectoryPage<MerchantResponse>([target], false));
        _staff.Setup(s => s.MergePreviewAsync(source.Id, target.Id)).ReturnsAsync(new MerchantMergePreview(source, target, 42, 3, 2));
        var page = Render<MerchantMaintenance>(p => p.Add(c => c.Merchant, source));
        var search = page.FindComponents<MudBlazor.MudTextField<string>>()[1];
        await page.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("Corner"));
        await page.FindAll("form")[1].SubmitAsync();
        await page.Find(".staff-merge-target").ClickAsync();
        Assert.Contains("42 expenses", page.Markup);
        Assert.True(page.Find(".staff-danger").HasAttribute("disabled"));
        _staff.Verify(s => s.MergeAsync(It.IsAny<Guid>(), It.IsAny<MergeMerchantsRequest>()), Times.Never);
    }
}
