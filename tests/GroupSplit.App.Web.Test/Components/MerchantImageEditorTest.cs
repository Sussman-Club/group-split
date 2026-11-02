using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class MerchantImageEditorTest : ComponentTest
{
    private readonly Mock<IStaffClient> _staff = new();
    private static MerchantResponse Merchant => new(Guid.NewGuid(), "Corner Market", null, DateTimeOffset.UtcNow, 4);

    public MerchantImageEditorTest()
    {
        Services.AddSingleton(_staff.Object);
        Render<MudPopoverProvider>();
    }

    [Fact]
    public async Task A_selected_logo_is_previewed_and_saved_with_the_name()
    {
        var merchant = Merchant;
        var editor = Render<MerchantDirectoryEditor>(p => p.Add(c => c.Merchant, merchant));
        await editor.FindAll(".staff-logo-option").Single(b => b.TextContent.Trim() == "Lidl").ClickAsync(new());
        var url = MerchantLogos.BundledPath + "lidl.svg";
        Assert.Equal(url, editor.Find(".staff-logo-preview img").GetAttribute("src"));
        Assert.True(editor.Instance.HasUnsavedChanges);
        await editor.Find(".staff-field input").ChangeAsync(new() { Value = "Lidl Market" });
        await editor.Find("form").SubmitAsync();
        _staff.Verify(s => s.SaveMerchantAsync(merchant.Id, It.Is<UpdateMerchantRequest>(r => r.Name == "Lidl Market" && r.LogoUrl == url)), Times.Once);
    }

    [Fact]
    public async Task Removing_the_image_saves_a_null_logo_and_shows_initials()
    {
        var merchant = Merchant with { LogoUrl = MerchantLogos.BundledPath + "lidl.svg" };
        var editor = Render<MerchantDirectoryEditor>(p => p.Add(c => c.Merchant, merchant));
        await editor.Find(".staff-logo-remove").ClickAsync(new());
        Assert.Empty(editor.FindAll(".staff-logo-preview img"));
        await editor.Find("form").SubmitAsync();
        _staff.Verify(s => s.SaveMerchantAsync(merchant.Id, It.Is<UpdateMerchantRequest>(r => r.LogoUrl == null)), Times.Once);
    }

    [Fact]
    public async Task A_pending_upload_is_local_until_save_and_uses_the_image_endpoint()
    {
        var merchant = Merchant;
        byte[] bytes = [1, 2, 3];
        var editor = Render<MerchantDirectoryEditor>(p => p.Add(c => c.Merchant, merchant));
        var picker = editor.FindComponent<MerchantLogoPicker>();
        await editor.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(MerchantLogoSelection.FromUpload(bytes)));
        Assert.StartsWith("data:image/png;base64,", editor.Find(".staff-logo-preview img").GetAttribute("src"));
        _staff.Verify(s => s.SaveMerchantImageAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<Guid?>()), Times.Never);
        await editor.Find("form").SubmitAsync();
        _staff.Verify(s => s.SaveMerchantImageAsync(merchant.Id, merchant.Name, bytes, merchant.Version), Times.Once);
        _staff.Verify(s => s.SaveMerchantAsync(It.IsAny<Guid?>(), It.IsAny<UpdateMerchantRequest>()), Times.Never);
        Assert.False(editor.Instance.HasUnsavedChanges);
    }

    [Fact]
    public async Task An_untrusted_url_is_not_loaded_and_cannot_be_saved()
    {
        var editor = Render<MerchantDirectoryEditor>(p => p.Add(c => c.Merchant, Merchant));
        var url = editor.FindComponent<MerchantLogoPicker>().FindComponent<MudTextField<string>>();
        await editor.InvokeAsync(() => url.Instance.ValueChanged.InvokeAsync("https://untrusted.example/image.png"));
        Assert.Empty(editor.FindAll(".staff-logo-preview img"));
        Assert.True(editor.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.Contains("not supported", editor.Markup);
    }

    [Fact]
    public void Uploaded_images_resolve_against_the_public_origin_on_native()
    {
        var url = MerchantLogos.UploadedPath + Guid.NewGuid().ToString("N") + ".png";
        var mark = Render<MerchantMark>(p => p.Add(c => c.MerchantName, "Shop").Add(c => c.MerchantLogoUrl, url));
        Assert.Equal(url, mark.Find("img").GetAttribute("src"));
        Assert.Equal("https://groupsplit.app" + url, MerchantLogos.Resolve(url, new Uri("https://groupsplit.app/")));
        Assert.False(MerchantLogos.IsAllowed(url + "?track=1"));
        Assert.False(MerchantLogos.IsAllowed(MerchantLogos.UploadedPath + "../receipt.png"));
    }
}
