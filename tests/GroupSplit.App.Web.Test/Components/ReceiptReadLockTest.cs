using Bunit;
using GroupSplit.App.Shared.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public sealed class ReceiptReadLockTest : ComponentTest
{
    [Fact]
    public void Reading_disables_actions_and_dialog_dismissal_and_restores_the_original_options()
    {
        var original = new DialogOptions { CloseButton = true, BackdropClick = true, CloseOnEscapeKey = true };
        var dialog = new Mock<IMudDialogInstance>();
        dialog.SetupGet(instance => instance.Options).Returns(original);
        dialog.Setup(instance => instance.SetOptionsAsync(It.IsAny<DialogOptions>())).Returns(Task.CompletedTask);
        var component = Render<ReceiptReadLock>(parameters => parameters
            .AddCascadingValue(dialog.Object)
            .Add(locker => locker.IsLocked, true)
            .AddChildContent("<button type='button'>Edit receipt</button><input /><a href='/'>Leave</a>"));

        Assert.True(component.Find("fieldset").HasAttribute("disabled"));
        Assert.True(component.Find("fieldset").HasAttribute("inert"));
        dialog.Verify(instance => instance.SetOptionsAsync(It.Is<DialogOptions>(options =>
            options.CloseButton == false && options.BackdropClick == false && options.CloseOnEscapeKey == false)), Times.Once);

        component.Render(parameters => parameters.Add(locker => locker.IsLocked, false));

        Assert.False(component.Find("fieldset").HasAttribute("disabled"));
        Assert.False(component.Find("fieldset").HasAttribute("inert"));
        dialog.Verify(instance => instance.SetOptionsAsync(original), Times.Once);
    }
}
