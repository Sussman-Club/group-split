using GroupSplit.API.Services;

namespace GroupSplit.API.Middleware;

public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IUserProvisioner provisioner,
        ICurrentUserInitializer currentUser)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var user = await provisioner.GetOrCreate(context.User, context.RequestAborted);
            currentUser.Initialize(user);
        }

        await next(context);
    }
}
