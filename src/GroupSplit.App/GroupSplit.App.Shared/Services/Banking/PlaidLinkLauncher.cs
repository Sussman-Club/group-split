using Microsoft.JSInterop;

namespace GroupSplit.App.Shared.Services.Banking;

/// <summary>
/// Opens the provider's own linking UI and waits for what it hands back.
/// </summary>
/// <remarks>
/// The one piece of JavaScript interop in the app that calls back into .NET, and it is here
/// rather than in a page so the object reference has an owner that disposes it. A page that
/// held its own would leak one per visit.
/// <para>
/// Plaid Link has to run in the browser: it is the only part of the flow this application
/// is not allowed to see, because the person's bank credentials are typed into Plaid's
/// frame and never touch this origin. What comes back is a one-time public token, which is
/// useless without the server's own credentials.
/// </para>
/// </remarks>
public sealed class PlaidLinkLauncher(IJSRuntime js) : IAsyncDisposable
{
    private DotNetObjectReference<Callback>? _callback;

    /// <summary>
    /// Shows the linking UI. Answers the public token when somebody links a bank, and null
    /// when they close it, when it fails, or when the browser cannot run it at all.
    /// </summary>
    public async Task<string?> OpenAsync(string linkToken)
    {
        var callback = new Callback();

        // Replaces any previous one: a person who opens Link, closes it and opens it again
        // should not be holding two.
        await DisposeCallbackAsync();
        _callback = DotNetObjectReference.Create(callback);

        try
        {
            await js.InvokeVoidAsync("gs.plaid.open", linkToken, _callback);
        }
        catch (Exception e) when (IsUnavailable(e))
        {
            // Prerendering, a dropped circuit, or a browser that could not fetch Plaid's
            // script. None of them is an error to shout about; there is simply no token.
            return null;
        }

        return await callback.Finished.Task;
    }

    public async ValueTask DisposeAsync() => await DisposeCallbackAsync();

    private ValueTask DisposeCallbackAsync()
    {
        _callback?.Dispose();
        _callback = null;

        return ValueTask.CompletedTask;
    }

    private static bool IsUnavailable(Exception e) =>
        e is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException;

    /// <summary>
    /// What the browser calls back into. Public and instance-methods-only because
    /// <see cref="JSInvokableAttribute"/> needs both.
    /// </summary>
    public sealed class Callback
    {
        internal TaskCompletionSource<string?> Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JSInvokable]
        public void OnSuccess(string publicToken) => Finished.TrySetResult(publicToken);

        /// <summary>
        /// Closing the UI is the ordinary way out of it, not a failure, so this answers null
        /// rather than throwing and nothing is shown.
        /// </summary>
        [JSInvokable]
        public void OnExit(string? errorCode) => Finished.TrySetResult(null);
    }
}
