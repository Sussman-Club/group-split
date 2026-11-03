using Bunit;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ScrollFeedTest : ComponentTest
{
    [Fact]
    public async Task Leaving_during_module_import_does_not_attach_an_observer_after_disposal()
    {
        var imported = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Mock<IJSObjectReference>();
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .Returns(new ValueTask<IJSObjectReference>(imported.Task));
        Services.AddSingleton(js.Object);
        var feed = Render<ScrollFeed<int>>(p => p
            .Add(x => x.ItemsProvider, _ => ValueTask.FromResult(new ItemsProviderResult<int>([1], 100)))
            .Add(x => x.ItemContent, value => b => b.AddContent(0, value)));

        var cleanup = feed.InvokeAsync(async () => await feed.Instance.DisposeAsync());
        imported.SetResult(module.Object);
        await cleanup;
        module.Verify(m => m.InvokeAsync<IJSObjectReference>("observe", It.IsAny<object?[]?>()), Times.Never);
        module.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_survives_an_already_disposed_observer_and_is_idempotent(bool disconnected)
    {
        var observer = new Mock<IJSObjectReference>();
        observer.Setup(o => o.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("dispose", It.IsAny<object?[]?>()))
            .Throws(disconnected ? new JSDisconnectedException("Gone") : new ObjectDisposedException("JSObjectReference"));
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.InvokeAsync<IJSObjectReference>("observe", It.IsAny<object?[]?>())).ReturnsAsync(observer.Object);
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>())).ReturnsAsync(module.Object);
        Services.AddSingleton(js.Object);
        var feed = Render<ScrollFeed<int>>(p => p
            .Add(x => x.ItemsProvider, _ => ValueTask.FromResult(new ItemsProviderResult<int>([1], 100)))
            .Add(x => x.ItemContent, value => b => b.AddContent(0, value)));

        await feed.InvokeAsync(async () => await feed.Instance.DisposeAsync());
        await feed.InvokeAsync(async () => await feed.Instance.DisposeAsync());
        observer.Verify(o => o.DisposeAsync(), Times.Once);
        module.Verify(m => m.DisposeAsync(), Times.Once);
    }

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
