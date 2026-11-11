using System.Net;
using GroupSplit.App.Shared.Localization;
using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace GroupSplit.App.Shared.Components;

/// <summary>Shared request feedback and draft protection for console editors.</summary>
public abstract class StaffEditorBase : ComponentBase
{
    [Inject] protected IStaffClient Client { get; set; } = null!;
    [Inject] private IJSRuntime Browser { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Text { get; set; } = null!;
    [Parameter] public EventCallback OnClose { get; set; }
    protected bool Saving { get; private set; }
    protected string? Error { get; private set; }
    protected string? Success { get; set; }
    protected ElementReference Heading;
    protected bool NavigatingAfterSave { get; set; }
    public abstract bool HasUnsavedChanges { get; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await Heading.FocusAsync(preventScroll: false);
    }

    public async Task<bool> CanDiscardAsync() => NavigatingAfterSave || !Saving && (!HasUnsavedChanges
        || await Browser.InvokeAsync<bool>("confirm", Text["Discard unsaved changes?"].Value));

    protected async Task CloseAsync()
    {
        if (await CanDiscardAsync()) await OnClose.InvokeAsync();
    }

    protected async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (!await CanDiscardAsync()) context.PreventNavigation();
    }

    protected async Task ExecuteAsync(Func<Task> action)
    {
        if (Saving) return;
        Saving = true;
        Error = Success = null;
        try { await action(); }
        catch (HttpRequestException e)
        {
            Error = e is ApiProblemException problem
                ? problem.UserMessage : Text[e.StatusCode switch
                {
                    HttpStatusCode.Conflict => "This record changed or the merchant name already exists. Refresh before trying again.",
                    HttpStatusCode.Forbidden => "Staff access is required. Your permissions may have changed.",
                    HttpStatusCode.BadRequest => "Check the entered values and try again.",
                    _ => "Could not complete the request. Please try again."
                }];
        }
        catch (TaskCanceledException) { Error = Text["The request timed out. Please try again."]; }
        finally { Saving = false; }
    }
}
