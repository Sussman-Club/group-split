using System.Security.Claims;
using GroupSplit.API.Middleware;
using GroupSplit.API.Services;
using Microsoft.AspNetCore.Http;
using Moq;

// 'User' alone binds to the enclosing GroupSplit.API.Test.User namespace.
using UserEntity = GroupSplit.Data.Entities.User;

namespace GroupSplit.API.Test.User;

/// <summary>
/// The middleware that puts the caller's <see cref="UserEntity"/> in front of every
/// endpoint. Small, and it sits on the authorization path: if it provisioned a user for
/// an unauthenticated request, every anonymous caller would get an account.
/// </summary>
public class CurrentUserMiddlewareTests
{
    private static HttpContext ContextFor(ClaimsPrincipal principal) =>
        new DefaultHttpContext { User = principal };

    private static ClaimsPrincipal Authenticated() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            authenticationType: "TestAuth"));

    /// <summary>An identity with no authentication type is not authenticated.</summary>
    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Fact]
    public async Task An_authenticated_request_has_its_user_provisioned_and_published()
    {
        var user = new UserEntity { Id = Guid.NewGuid() };
        var principal = Authenticated();

        var provisioner = new Mock<IUserProvisioner>();
        provisioner
            .Setup(p => p.GetOrCreate(principal, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var initializer = new Mock<ICurrentUserInitializer>();
        var nextWasCalled = false;

        var middleware = new CurrentUserMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ContextFor(principal), provisioner.Object, initializer.Object);

        initializer.Verify(i => i.Initialize(user), Times.Once);
        Assert.True(nextWasCalled);
    }

    [Fact]
    public async Task An_anonymous_request_is_passed_through_without_provisioning_anyone()
    {
        var provisioner = new Mock<IUserProvisioner>(MockBehavior.Strict);
        var initializer = new Mock<ICurrentUserInitializer>(MockBehavior.Strict);
        var nextWasCalled = false;

        var middleware = new CurrentUserMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });

        // Strict mocks: any call to either would throw rather than be quietly allowed.
        await middleware.InvokeAsync(
            ContextFor(Anonymous()), provisioner.Object, initializer.Object);

        Assert.True(nextWasCalled);
    }

    /// <summary>
    /// The pipeline continues either way. A middleware that returned early on the
    /// authenticated path would strand every request that needs a user.
    /// </summary>
    [Fact]
    public async Task The_rest_of_the_pipeline_runs_after_the_user_is_published()
    {
        var principal = Authenticated();
        var order = new List<string>();

        var provisioner = new Mock<IUserProvisioner>();
        provisioner
            .Setup(p => p.GetOrCreate(principal, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserEntity { Id = Guid.NewGuid() });

        var initializer = new Mock<ICurrentUserInitializer>();
        initializer
            .Setup(i => i.Initialize(It.IsAny<UserEntity>()))
            .Callback(() => order.Add("initialize"));

        var middleware = new CurrentUserMiddleware(_ =>
        {
            order.Add("next");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ContextFor(principal), provisioner.Object, initializer.Object);

        Assert.Equal(["initialize", "next"], order);
    }
}
