using GroupSplit.App.Shared.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ExtendedFabTest : ComponentTest
{
    [Fact(Timeout = 10_000)]
    public async Task Navigation_during_import_releases_the_module_without_observing_a_removed_button()
    {
        var imported = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new Mock<IJSObjectReference>(MockBehavior.Strict);
        module.Setup(value => value.DisposeAsync()).Callback(() => released.TrySetResult()).Returns(ValueTask.CompletedTask);
        var runtime = new Mock<IJSRuntime>();
        runtime.Setup(value => value.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .Returns(() => new ValueTask<IJSObjectReference>(imported.Task));
        Services.AddSingleton(runtime.Object);

        var button = Render<ExtendedFab>();
        runtime.Verify(value => value.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()), Times.Once);
        await button.Instance.DisposeAsync();
        imported.SetResult(module.Object);

        await released.Task.WaitAsync(TestContext.Current.CancellationToken);
        module.Verify(value => value.DisposeAsync(), Times.Once);
        Assert.DoesNotContain(module.Invocations, invocation => invocation.Method.Name == "InvokeAsync");
    }
}
