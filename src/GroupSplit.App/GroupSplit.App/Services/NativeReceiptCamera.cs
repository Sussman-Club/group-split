using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;

namespace GroupSplit.App.Services;

public sealed class NativeReceiptCamera : IReceiptCamera
{
    public async Task<IBrowserFile?> CaptureAsync()
    {
        var photo = await MainThread.InvokeOnMainThreadAsync(() => MediaPicker.Default.CapturePhotoAsync());
        if (photo is null)
            return null;

        const int maximumLength = 10 * 1024 * 1024;
        await using var source = await photo.OpenReadAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > maximumLength)
                throw new IOException("Choose a receipt photo no larger than 10 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count));
        }

        return new BufferedBrowserFile(photo.FileName, photo.ContentType, DateTimeOffset.UtcNow, buffer.ToArray());
    }
}
