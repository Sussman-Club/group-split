using System.Net;
using GroupSplit.App.Shared.Localization;
using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace GroupSplit.App.Shared.Components;

/// <summary>Consistent load feedback; older requests cannot replace a newer page of results.</summary>
public abstract class StaffPageBase : ComponentBase
{
    [Inject] protected IStaffClient Client { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Inject] private IStringLocalizer<AppStrings> Text { get; set; } = null!;
    protected bool Loading { get; private set; } = true;
    protected string? LoadError { get; private set; }
    private int _request;

    protected async Task LoadAsync<T>(Func<Task<T>> read, Action<T> apply)
    {
        var request = ++_request;
        Loading = true;
        LoadError = null;
        try
        {
            var result = await read();
            if (request == _request) apply(result);
        }
        catch (HttpRequestException e) when (request == _request)
        {
            LoadError = Text[e.StatusCode switch
            {
                HttpStatusCode.NotFound => "This record could not be found. It may have been removed.",
                HttpStatusCode.Forbidden => "Staff access is required. Your permissions may have changed.",
                _ => "Could not load this view. Check your connection and try again."
            }];
        }
        catch (TaskCanceledException) when (request == _request)
        {
            LoadError = Text["The request timed out. Please try again."];
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        finally { if (request == _request) Loading = false; }
    }
}
