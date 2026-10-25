using Bunit;
using GroupSplit.App.Shared;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Users;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class LoadingStateTest : ComponentTest
{
    [Theory]
    [InlineData(SkeletonLayout.Dashboard)]
    [InlineData(SkeletonLayout.Groups)]
    [InlineData(SkeletonLayout.Activity)]
    [InlineData(SkeletonLayout.Inbox)]
    [InlineData(SkeletonLayout.Summary)]
    [InlineData(SkeletonLayout.Rows)]
    public void Skeletons_replace_pending_content_and_leave_failures_retryable(SkeletonLayout layout)
    {
        var view = Render<LoadView>(p => p
            .Add(s => s.Loading, true)
            .Add(s => s.Skeleton, layout)
            .Add(s => s.Retry, () => { })
            .AddChildContent("<p id='loaded-content'>Ready</p>"));

        Assert.Single(view.FindAll("[role='status'][aria-busy='true']"));
        Assert.NotEmpty(view.FindAll("[aria-hidden='true'] .mud-skeleton"));
        Assert.Empty(view.FindAll("#loaded-content"));
        Assert.Empty(view.FindAll(".gs-load-spinner"));

        view.Render(p => p.Add(s => s.Loading, false).Add(s => s.Failed, true));
        Assert.Empty(view.FindAll(".mud-skeleton"));
        Assert.Single(view.FindAll("[role='alert']"));
        Assert.Single(view.FindAll("button"));

        view.Render(p => p.Add(s => s.Failed, false));
        Assert.Single(view.FindAll("#loaded-content"));
        Assert.Empty(view.FindAll(".mud-skeleton"));
    }

    [Fact]
    public void Pending_authentication_does_not_instantiate_the_routes()
    {
        Snackbar.Setup(s => s.Configuration).Returns(new SnackbarConfiguration());
        Services.AddMudTheme();
        Services.AddSingleton<ThemePreference>();
        var answer = new TaskCompletionSource();
        var login = new Mock<IUserLogin>();
        login.Setup(s => s.RefreshLoginAsync()).Returns(answer.Task);
        Services.AddSingleton(login.Object);

        var app = Render<CascadingUserLogin>(p => p.AddChildContent("<p id='routes'>Routes</p>"));

        Assert.Empty(app.FindAll("#routes"));
        Assert.Single(app.FindAll("[role='status'][aria-busy='true']"));
        answer.SetResult();
        app.WaitForAssertion(() => Assert.Single(app.FindAll("#routes")));
    }

    [Fact]
    public void A_failed_view_can_retry_and_only_renders_content_when_ready()
    {
        var retried = false;
        var view = Render<LoadView>(p => p
            .Add(s => s.Loading, true)
            .Add(s => s.Failed, true)
            .Add(s => s.Retry, () => retried = true)
            .AddChildContent("<p id='data'>Loaded data</p>"));

        Assert.Empty(view.FindAll("#data"));
        Assert.Empty(view.FindAll("button"));
        view.Render(p => p.Add(s => s.Loading, false));
        Assert.Empty(view.FindAll("#data"));
        view.Find("button").Click();
        Assert.True(retried);
        view.Render(p => p.Add(s => s.Failed, false));
        Assert.Single(view.FindAll("#data"));
    }

    [Fact]
    public async Task An_unavailable_user_endpoint_is_retryable_instead_of_signed_out()
    {
        var users = new Mock<IUsersClient>();
        users.SetupSequence(s => s.GetCurrentUserAsync())
            .ThrowsAsync(new HttpRequestException())
            .ReturnsAsync(new UserInfo(Guid.NewGuid(), "Ada", "Lovelace", "ada@example.com"));
        var login = new UserLogin(new UserTracker(), users.Object);

        await login.RefreshLoginAsync();
        Assert.True(login.LoginFailed);
        Assert.Null(login.User);
        await login.RefreshLoginAsync();
        Assert.False(login.LoginFailed);
        Assert.NotNull(login.User);
    }
}
