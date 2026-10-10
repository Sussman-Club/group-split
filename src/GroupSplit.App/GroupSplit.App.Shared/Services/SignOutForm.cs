using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Signs out by posting a form to <see cref="AuthRoutes.Logout"/>, shared by the server
/// and WebAssembly auth services so the two cannot drift apart.
/// </summary>
/// <remarks>
/// A POST, because a GET that signs out is one any other site can trigger with an image
/// tag. A form rather than a fetch, because the answer is a redirect to Keycloak's
/// end-session page that the browser has to follow as a page load. The antiforgery token
/// goes with it: the endpoint requires one, and only a page this app rendered has it.
/// </remarks>
public static class SignOutForm
{
    public static async Task PostAsync(IJSRuntime js, AntiforgeryStateProvider antiforgery, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>();

        if (antiforgery.GetAntiforgeryToken() is { } token)
        {
            fields[token.FormFieldName] = token.Value;
        }

        await js.InvokeVoidAsync("gs.postForm", ct, AuthRoutes.Logout, fields);
    }
}
