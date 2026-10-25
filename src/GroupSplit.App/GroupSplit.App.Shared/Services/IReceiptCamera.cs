using Microsoft.AspNetCore.Components.Forms;

namespace GroupSplit.App.Shared.Services;

public interface IReceiptCamera
{
    Task<IBrowserFile?> CaptureAsync();
}
