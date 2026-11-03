using Bunit;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace GroupSplit.App.Web.Test.Components;

public class ScrollFeedTest : ComponentTest
{
    [Fact]
    public async Task Large_history_keeps_only_the_requested_window_and_refreshes()
    {
        var starts = new List<int>();
        var feed = Render<ScrollFeed<int>>(p => p
            .Add(x => x.ItemsProvider, request =>
            {
                starts.Add(request.StartIndex);
                return ValueTask.FromResult(new ItemsProviderResult<int>(
                    Enumerable.Range(request.StartIndex, request.Count), 100000));
            })
            .Add(x => x.ItemContent, value => builder => builder.AddContent(0, value)));
        var virtualize = feed.FindComponent<Virtualize<int>>();
        var result = await virtualize.Instance.ItemsProvider!(new ItemsProviderRequest(50000, 20, default));
        Assert.Equal(20, result.Items.Count());
        Assert.Equal(50000, result.Items.First());
        Assert.Equal(100000, result.TotalItemCount);
        Assert.Equal(5, virtualize.Instance.OverscanCount);
        Assert.DoesNotContain("Load more", feed.Markup);
        await feed.InvokeAsync(feed.Instance.RefreshDataAsync);
    }
}
