using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// What the app's sign-out buttons send, through both auth services: a form post carrying
/// the page's antiforgery token, never a navigation to a GET.
/// </summary>
public class SignOutFormTest
{
    [Fact]
    public async Task Signing_out_posts_the_logout_form_with_the_pages_antiforgery_token()
    {
        var js = new RecordingJs();

        await SignOutForm.PostAsync(js, new Antiforgery(new AntiforgeryRequestToken("a-token", "__RequestVerificationToken")),
            TestContext.Current.CancellationToken);

        var (identifier, args) = Assert.Single(js.Calls);

        Assert.Equal("gs.postForm", identifier);
        Assert.Equal(AuthRoutes.Logout, args[0]);
        Assert.Equal(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = "a-token" },
            Assert.IsType<Dictionary<string, string>>(args[1]));
    }

    /// <summary>
    /// Posted anyway without one: the server refuses it, which is the right answer for a page
    /// that cannot prove it is the app's, and nothing is gained by not asking.
    /// </summary>
    [Fact]
    public async Task Without_a_token_the_form_is_posted_empty()
    {
        var js = new RecordingJs();

        await SignOutForm.PostAsync(js, new Antiforgery(null), TestContext.Current.CancellationToken);

        var (_, args) = Assert.Single(js.Calls);

        Assert.Empty(Assert.IsType<Dictionary<string, string>>(args[1]));
    }

    [Fact]
    public async Task Both_auth_services_sign_out_through_the_form()
    {
        var js = new RecordingJs();

        await new Services.AuthService(new Nav(), js, Antiforgery.Any).Logout(TestContext.Current.CancellationToken);
        await new Client.Services.AuthService(new Nav(), js, Antiforgery.Any).Logout(TestContext.Current.CancellationToken);

        Assert.Equal(["gs.postForm", "gs.postForm"], js.Calls.Select(call => call.Identifier));
    }

    private sealed class Nav : NavigationManager
    {
        public Nav() => Initialize("https://groupsplit.test/", "https://groupsplit.test/");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException($"Navigated to {uri} instead of posting a form.");
    }

    private sealed class Antiforgery(AntiforgeryRequestToken? token) : AntiforgeryStateProvider
    {
        public static Antiforgery Any { get; } = new(new AntiforgeryRequestToken("a-token", "__RequestVerificationToken"));

        public override AntiforgeryRequestToken? GetAntiforgeryToken() => token;
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public List<(string Identifier, object?[] Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));

            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
