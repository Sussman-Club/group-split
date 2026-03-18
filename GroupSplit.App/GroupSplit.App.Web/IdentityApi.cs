namespace GroupSplit.App.Web;

public static class IdentityApi
{
    public static RouteGroupBuilder MapIdentity(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth");

        group.MapLogin();
        group.MapRegister();
        group.MapLogout();

        return group;
    }

    private static void MapRegister(this RouteGroupBuilder group)
    {
        group.MapGet("/register", (HttpContext httpContext) =>
        {
            throw new NotImplementedException("Registration is not supported on server-side. Please invoke registration from the client-side.");
        });
    }

    private static void MapLogin(this RouteGroupBuilder group)
    {
        group.MapGet("/login", (HttpContext httpContext) =>
        {
            throw new NotImplementedException("Login is not supported on server-side. Please invoke login from the client-side.");
        });
    }

    private static void MapLogout(this RouteGroupBuilder group)
    {
        group.MapGet("/logout", (HttpContext httpContext) =>
        {
            throw new NotImplementedException("Logout is not supported on server-side. Please invoke logout from the client-side.");
        });
    }
}