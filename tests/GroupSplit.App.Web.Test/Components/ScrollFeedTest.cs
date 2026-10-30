using Bunit;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace GroupSplit.App.Web.Test.Components;

public class ScrollFeedTest : ComponentTest
{
    [Fact]
    public async Task Loading_more_appends_rows_and_refresh_replaces_the_old_results()
    {
        var starts = new List<int>();
        ItemsProviderDelegate<int> provider = request =>
        {
            starts.Add(request.StartIndex);
            var items = Enumerable.Range(request.StartIndex, Math.Min(request.Count, 73 - request.StartIndex));
            return ValueTask.FromResult(new ItemsProviderResult<int>(items, 73));
        };
        RenderFragment<int> item = value => builder => builder.AddContent(0, value.ToString() + " ");
        var feed = Render<ScrollFeed<int>>(p => p.Add(x => x.ItemsProvider, provider).Add(x => x.ItemContent, item));
        Assert.Equal([0], starts);
        await feed.InvokeAsync(feed.Instance.LoadMoreAsync);
        Assert.Equal([0, 50], starts);
        Assert.Empty(feed.FindAll(".gs-feed-sentinel"));
        Assert.Contains("72", feed.Markup);
        await feed.InvokeAsync(feed.Instance.RefreshDataAsync);
        Assert.Equal([0, 50, 0], starts);
        Assert.DoesNotContain("72 ", feed.Markup);
        Assert.Single(feed.FindAll(".gs-feed-sentinel"));
    }
}
