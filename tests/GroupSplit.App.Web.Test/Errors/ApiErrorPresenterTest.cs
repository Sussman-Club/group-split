using GroupSplit.App.Shared.Services.Errors;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Errors;

/// <summary>
/// Where a 401 sends somebody. The presenter is the only place in the app that decides
/// this, so the decision is worth pinning on its own.
/// </summary>
public class ApiErrorPresenterTest
{
    /// <summary>
    /// To the hand-off, and not to <c>/auth/login</c>. A challenge would send the person
    /// through Keycloak, sign them back in, and only then land them on a page saying their
    /// session had expired -- two trips through sign-in to be told the opposite of what had
    /// just happened, with the "Keep me signed in" question asked too late to govern the
    /// session the first trip created.
    /// </summary>
    [Fact]
    public async Task A_401_goes_to_the_hand_off_rather_than_straight_to_a_challenge()
    {
        var nav = At("https://localhost/groups/42");

        await Presenter(nav).ShowAsync(Unauthenticated());

        Assert.Equal("/login?reason=session-expired&returnUrl=%2Fgroups%2F42", nav.Went);
    }

    /// <summary>
    /// The page they were on is the one they meant to be on. Losing it turns an expiry into
    /// starting over.
    /// </summary>
    [Fact]
    public async Task The_page_they_were_on_comes_back_with_them()
    {
        var nav = At("https://localhost/transactions?month=2026-09");

        await Presenter(nav).ShowAsync(Unauthenticated());

        Assert.Contains(
            $"returnUrl={Uri.EscapeDataString("/transactions?month=2026-09")}",
            nav.Went,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason travels as a code, never as the sentence. Anything the URL carries is
    /// something a stranger can choose, and the hand-off renders it.
    /// </summary>
    [Fact]
    public async Task The_wording_is_not_what_travels_in_the_url()
    {
        var nav = At("https://localhost/groups/42");

        await Presenter(nav).ShowAsync(Unauthenticated());

        Assert.DoesNotContain(
            ErrorMessages.SessionExpired,
            Uri.UnescapeDataString(nav.Went!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything that is not a 401 is said where the person already is.
    /// </summary>
    [Fact]
    public async Task Any_other_failure_leaves_them_where_they_are()
    {
        var nav = At("https://localhost/groups/42");

        await Presenter(nav).ShowAsync(new ApiError(ApiErrorKind.Network, ErrorMessages.Network));

        Assert.Null(nav.Went);
    }

    private static ApiErrorPresenter Presenter(NavigationManager nav) =>
        new(nav, Mock.Of<ISnackbar>());

    private static RecordingNavigationManager At(string uri) => new(uri);

    private static ApiError Unauthenticated() =>
        new(ApiErrorKind.Unauthenticated, ErrorMessages.SessionExpired);

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public RecordingNavigationManager(string uri) => Initialize("https://localhost/", uri);

        public string? Went { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad) => Went = uri;
    }
}
