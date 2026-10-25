using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using GroupSplit.App.Shared.Services;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptFilePickerTest : ComponentTest
{
    [Fact]
    public async Task Native_capture_delivers_a_readable_file_to_the_same_selection_callback()
    {
        var file = new BufferedBrowserFile("camera.jpg", "image/jpeg", DateTimeOffset.UtcNow, [1, 2, 3]);
        var camera = new Mock<IReceiptCamera>();
        camera.Setup(service => service.CaptureAsync()).ReturnsAsync(file);
        Services.AddSingleton(camera.Object);
        IBrowserFile? selected = null;
        var picker = Render<ReceiptFilePicker>(parameters => parameters
            .Add(component => component.FileChanged, EventCallback.Factory.Create<InputFileChangeEventArgs>(this,
                args => selected = args.File)));

        await picker.Find("button.gs-file-picker-camera-action").ClickAsync(new());

        Assert.Same(file, selected);
        Assert.Single(picker.FindAll("input[type=file]"));
        camera.Verify(service => service.CaptureAsync(), Times.Once);
    }

    [Fact]
    public async Task Cancelling_native_capture_does_not_select_a_file_or_show_an_error()
    {
        var camera = new Mock<IReceiptCamera>();
        camera.Setup(service => service.CaptureAsync()).ReturnsAsync((IBrowserFile?)null);
        Services.AddSingleton(camera.Object);
        var picker = Render<ReceiptFilePicker>();
        await picker.Find("button.gs-file-picker-camera-action").ClickAsync(new());
        Assert.Empty(picker.FindAll(".mud-alert"));
    }

    [Fact]
    public void Browser_camera_input_is_the_tap_target_and_survives_selection()
    {
        var picker = Render<ReceiptFilePicker>();
        var input = picker.Find(".gs-file-picker-camera-action input[type=file]");
        Assert.Equal("environment", input.GetAttribute("capture"));
        Assert.Equal("image/*", input.GetAttribute("accept"));
        picker.Render(parameters => parameters.Add(component => component.IsAttached, true));
        Assert.NotNull(picker.Find(".gs-file-picker-camera-action input[type=file]"));
        Assert.True(picker.Find(".gs-file-picker-dropzone").HasAttribute("hidden"));
    }

    [Fact]
    public async Task The_read_button_invokes_the_callback_without_submitting_and_keeps_the_input_mounted()
    {
        var called = false;
        var picker = Render<ReceiptFilePicker>(parameters => parameters
            .Add(component => component.IsAttached, true)
            .Add(component => component.FileName, "receipt.pdf")
            .Add(component => component.ReadRequested,
                EventCallback.Factory.Create(this, () => called = true)));

        var input = picker.Find("input[type=file]");
        var button = picker.Find("button.gs-file-picker-read");

        Assert.Equal("receipt.pdf", picker.Find(".gs-file-picker-attached .gs-file-picker-title").TextContent);
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.NotNull(input);

        await picker.InvokeAsync(() => button.Click());

        Assert.True(called);
        Assert.NotNull(picker.Find("input[type=file]"));
    }

    [Fact]
    public async Task A_buffered_file_can_be_read_after_the_picker_is_disposed()
    {
        var file = new BufferedBrowserFile("receipt.pdf", "application/pdf", DateTimeOffset.UtcNow,
            "receipt"u8.ToArray());

        await using var stream = file.OpenReadStream(10 * 1024 * 1024);
        using var reader = new StreamReader(stream);

        Assert.Equal("receipt", await reader.ReadToEndAsync());
        Assert.Equal("receipt.pdf", file.Name);
        Assert.Equal("application/pdf", file.ContentType);
    }
}
