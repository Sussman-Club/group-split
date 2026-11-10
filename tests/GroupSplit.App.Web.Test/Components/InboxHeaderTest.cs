using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Pages;
using Microsoft.AspNetCore.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Banking;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class InboxHeaderTest : ComponentTest
{
    [Fact]
    public async Task Both_views_and_navigation_share_live_waiting_counts()
    {
        var bank = new Mock<IInboxStateService>();
        bank.SetupGet(x => x.NewCount).Returns(5);
        Services.AddSingleton(bank.Object);
        var receipts = new Mock<IReceiptsClient>();
        receipts.Setup(x => x.ListReceiptInboxAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new(), []),
            new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new(), []),
            new ReceiptInboxResponse(Guid.NewGuid(), null, "USD", new(), [])
        });
        Services.AddSingleton(receipts.Object);
        var header = Render<InboxHeader>();
        var badge = Render<InboxHeader>(p => p.Add(x => x.BadgeOnly, true));
        header.WaitForAssertion(() => Assert.Contains("8 purchases waiting", header.Markup));
        Assert.Equal("8", badge.Find(".gs-nav-badge").TextContent);
        Assert.Single(header.FindAll("a[href='/inbox']"));
        Assert.Single(header.FindAll("a[href='/inbox/receipts']"));
        Assert.Single(header.FindAll("a[href='/inbox/bank']"));
        await header.InvokeAsync(() => Services.GetRequiredService<InboxOverview>().SetReceiptCount(2));
        header.WaitForAssertion(() => Assert.Contains("7 purchases waiting", header.Markup));
        Assert.Equal("7", badge.Find(".gs-nav-badge").TextContent);
    }
    [Fact]
    public void Overview_links_both_queues_and_shows_waiting_counts()
    {
        var bank = new Mock<IInboxStateService>();
        bank.SetupGet(x => x.NewCount).Returns(3);
        Services.AddSingleton(bank.Object);
        var page = Render<InboxHome>();
        page.WaitForAssertion(() => Assert.Contains("3 waiting", page.Markup));
        Assert.Contains("0 waiting", page.Markup);
        Assert.NotEmpty(page.FindAll("a[href='/inbox/receipts']"));
        Assert.NotEmpty(page.FindAll("a[href='/inbox/bank']"));
    }

    [Fact]
    public void Old_filtered_inbox_links_open_the_bank_queue()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/inbox?status=added&q=coffee");
        Render<InboxHome>();
        Assert.EndsWith("/inbox/bank?status=added&q=coffee", nav.Uri);
    }
}
