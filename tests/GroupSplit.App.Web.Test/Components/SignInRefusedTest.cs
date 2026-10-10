using Bunit;
using GroupSplit.App.Shared;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Somebody recreated in Keycloak signs in with an email another account holds, and the
/// API refuses every request they make with <c>IDENTITY_EMAIL_IN_USE</c>. The app says so
/// once, in place of itself, rather than rendering pages that each fail to load.
/// </summary>
public class SignInRefusedTest : ComponentTest
{
    private const string TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    private readonly Mock<IUsersClient> _users = new();
    private readonly Mock<IAuthService> _auth = new();

    public SignInRefusedTest()
    {
        // The refusal renders in the full-screen layout, whose theme provider hosts the
        // snackbar provider, and that reads the configuration on render.
        Snackbar.Setup(snackbar => snackbar.Configuration).Returns(new SnackbarConfiguration());

        Services.AddMudTheme();
        Services.AddSingleton<ThemePreference>();
        Services.AddSingleton(_auth.Object);
        Services.AddSingleton(new UserTracker());
        Services.AddSingleton(_users.Object);
        Services.AddSingleton<IUserLogin, UserLogin>();
    }

    private IRenderedComponent<CascadingUserLogin> RenderApp() =>
        Render<CascadingUserLogin>(parameters => parameters
            .Add(login => login.ChildContent, (RenderFragment)(builder =>
                builder.AddMarkupContent(0, "<p id=\"the-app\">The app</p>"))));

    [Fact]
    public void A_refused_sign_in_shows_why_and_the_reference_instead_of_the_app()
    {
        _users.Setup(client => client.GetCurrentUserAsync())
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.IdentityEmailInUse));

        var app = RenderApp();

        Assert.Empty(app.FindAll("#the-app"));
        Assert.Contains("We could not open your account", app.Markup);
        Assert.Contains(ErrorMessages.For(ErrorCodes.IdentityEmailInUse), app.Markup);
        Assert.Contains(TraceId, app.Markup);
    }

    [Fact]
    public void Signing_out_is_offered_and_works()
    {
        _users.Setup(client => client.GetCurrentUserAsync())
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.IdentityEmailInUse));

        var app = RenderApp();

        app.FindAll("button").Single(button => button.TextContent.Contains("Sign out")).Click();

        _auth.Verify(service => service.Logout(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The refusal page asks nothing of the API, so it cannot be refused into a loop: the
    /// one call that found the refusal is the only one made.
    /// </summary>
    [Fact]
    public void The_refusal_is_asked_for_once_and_not_again()
    {
        _users.Setup(client => client.GetCurrentUserAsync())
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.IdentityEmailInUse));

        var app = RenderApp();
        app.Render();

        _users.Verify(client => client.GetCurrentUserAsync(), Times.Once);
    }

    [Fact]
    public void A_signed_in_person_gets_the_app()
    {
        _users.Setup(client => client.GetCurrentUserAsync())
            .ReturnsAsync(new UserInfo(Guid.NewGuid(), "Ada", "Lovelace", "ada@example.com"));

        Assert.Single(RenderApp().FindAll("#the-app"));
    }

    /// <summary>
    /// Any other failure to read the user is what it always was: no user, and the app --
    /// whose pages then deal with the 401 or the outage the way they already do.
    /// </summary>
    [Theory]
    [InlineData(401, ErrorCodes.Unauthenticated)]
    [InlineData(500, ErrorCodes.InternalError)]
    [InlineData(409, ErrorCodes.Conflict)]
    public void Other_failures_are_not_a_refused_sign_in(int status, string code)
    {
        _users.Setup(client => client.GetCurrentUserAsync())
            .ThrowsAsync(Refusals.Of(status, code));

        Assert.Single(RenderApp().FindAll("#the-app"));
        Assert.Null(Services.GetRequiredService<IUserLogin>().Refusal);
    }
}
