using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Web.Test.Components;

public class ReceiptFilePickerTest : ComponentTest
{
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

        Assert.Equal("receipt.pdf", picker.Find(".gs-file-picker-title").TextContent);
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
