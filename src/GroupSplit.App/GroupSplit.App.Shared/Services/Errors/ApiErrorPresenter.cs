using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Errors;

/// <summary>
/// Owns what happens when an API call fails. Every page state service and component that
/// talks to the API runs the call through here, so the three cases that are not domain
/// errors have one defined behaviour: a 401 sends the person to sign in again, a network
/// failure says the server could not be reached, an unknown 5xx apologises and quotes the
/// trace id. A domain refusal shows the message its code maps to. Anything that is not an
/// API failure at all is rethrown, because that is a bug and the error boundary is for bugs.
/// </summary>
public sealed class ApiErrorPresenter(IAuthService auth, NavigationManager nav, ISnackbar snackbar)
{
    /// <summary>
    /// Runs the call and reads a failure without showing it, for a caller that wants to
    /// put the message somewhere of its own -- inline in a dialog that should stay open.
    /// </summary>
    /// <returns>Null when the call completed.</returns>
    public async Task<ApiError?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception) when (ApiErrors.IsCancellation(exception))
        {
            // The caller went away mid-flight. Not a failure, and nobody is there to tell.
            return new ApiError(ApiErrorKind.Unknown, string.Empty);
        }
        catch (Exception exception) when (ApiErrors.IsApiFailure(exception))
        {
            return ApiErrors.Read(exception);
        }
    }

    /// <summary>
    /// Runs the call and shows a failure the way its kind calls for.
    /// </summary>
    /// <param name="context">
    /// What was being attempted, as a sentence to go in front of the reason:
    /// "Could not create the group."
    /// </param>
    /// <returns>Whether the call completed.</returns>
    public async Task<bool> TryAsync(Func<Task> action, string? context = null)
    {
        var error = await CaptureAsync(action);

        if (error is null) return true;

        await ShowAsync(error, context);
        return false;
    }

    public async Task ShowAsync(ApiError error, string? context = null)
    {
        switch (error.Kind)
        {
            case ApiErrorKind.Unauthenticated:
                await SignInAgainAsync();
                return;

            case ApiErrorKind.Unknown when error.Message.Length == 0:
                // A cancellation. Nothing to say.
                return;

            default:
                // Leads with what did not happen, then why; and stays until dismissed,
                // because an error about the thing someone just tried to do is not
                // something to let scroll away.
                snackbar.Add(
                    string.IsNullOrEmpty(context) ? error.Message : $"{context} {error.Message}",
                    Severity.Error);
                return;
        }
    }

    /// <summary>
    /// The BFF answers 401 once the session behind the cookie is gone (it expired, or the
    /// server restarted and dropped its ticket store). Nothing here can recover that;
    /// sign-in can, and it brings the person back to where they were.
    /// </summary>
    private async Task SignInAgainAsync()
    {
        try
        {
            await auth.Login("/" + nav.ToBaseRelativePath(nav.Uri));
        }
        catch (Exception)
        {
            // A navigation exception is how a prerender redirects; nothing to do with it here.
        }
    }
}
