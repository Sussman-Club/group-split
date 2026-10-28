using System.Globalization;
using System.Security.Claims;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Routing;

public sealed class LanguageRequestTest
{
    [Theory]
    [InlineData("pt", "es", "en-US", "pt")]
    [InlineData("es", null, "en-US", "es")]
    [InlineData(null, "pt", "es-MX", "pt")]
    [InlineData(null, null, "es-MX", "es")]
    [InlineData(null, null, "pt-BR", "pt")]
    [InlineData(null, null, "de-DE", "en")]
    [InlineData(null, null, "de-DE,es;q=0.9", "es")]
    public async Task Account_then_cookie_then_supported_browser_language_are_resolved_before_rendering(
        string? account, string? cookie, string browser, string expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWebLocalization();
        var users = new Mock<IUsersClient>();
        users.Setup(x => x.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserInfo(Guid.NewGuid(), "Ana", null, null, PreferredLanguage: account));
        builder.Services.AddSingleton(users.Object);
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "test"));
            return next(context);
        });
        app.UseRequestLocalization();
        app.MapGet("/", () => CultureInfo.CurrentCulture.Name + ":" + CultureInfo.CurrentUICulture.Name);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Accept", "text/html");
        client.DefaultRequestHeaders.Add("Accept-Language", browser);
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Culture=c=" + cookie + "|uic=" + cookie);
        Assert.Equal(expected + ":" + expected, await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }
}
