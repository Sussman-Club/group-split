using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// The sign-in handshake rides entirely on cookies, and a browser drops the ones
/// it is not willing to store without ever telling the server. These pin the
/// attributes for both deployment shapes: the plain-HTTP one we run today, and the
/// TLS-terminated one the strict defaults are written for.
/// </summary>
public class SignInCookieTest
{
    private const string PlainHttpAuthority = "http://keycloak.test/realms/group-split";

    private const string HttpsAuthority = "https://keycloak.test/realms/group-split";

    [Fact]
    public async Task Over_plain_http_the_handshake_cookies_are_ones_a_browser_will_store()
    {
        await using var host = await SignInHost.StartAsync(PlainHttpAuthority);

        var response = await host.Client.GetAsync("/auth/login", TestContext.Current.CancellationToken);

        var cookies = SignInHost.HandshakeCookies(response);

        Assert.Equal(2, cookies.Count);
        Assert.All(cookies, cookie =>
        {
            var attributes = SignInHost.AttributesOf(cookie);

            // The whole bug: a `Secure` cookie arriving over plain HTTP is discarded,
            // and the callback then has no correlation cookie left to check.
            Assert.DoesNotContain("secure", attributes, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", attributes, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Over_plain_http_the_callback_is_asked_for_as_a_top_level_get()
    {
        await using var host = await SignInHost.StartAsync(PlainHttpAuthority);

        var response = await host.Client.GetAsync("/auth/login", TestContext.Current.CancellationToken);

        // form_post returns the code as a cross-site POST, which a Lax cookie does not
        // ride along with -- so relaxing the cookies is only half the fix.
        Assert.NotEqual("form_post", SignInHost.AuthorizeParameter(response, "response_mode"));
    }

    [Fact]
    public async Task Over_https_the_handler_keeps_its_strict_defaults()
    {
        await using var host = await SignInHost.StartAsync(HttpsAuthority);

        var response = await host.Client.GetAsync("/auth/login", TestContext.Current.CancellationToken);

        var cookies = SignInHost.HandshakeCookies(response);

        Assert.Equal(2, cookies.Count);
        Assert.All(cookies, cookie =>
        {
            var attributes = SignInHost.AttributesOf(cookie);

            Assert.Contains("secure", attributes, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("samesite=none", attributes, StringComparer.OrdinalIgnoreCase);
        });

        // Putting TLS in front has to restore the cross-site POST callback along with
        // the cookies that survive one, or the relaxation quietly becomes permanent.
        Assert.Equal("form_post", SignInHost.AuthorizeParameter(response, "response_mode"));
    }

    [Theory]
    [InlineData(PlainHttpAuthority, CookieSecurePolicy.SameAsRequest)]
    [InlineData(HttpsAuthority, CookieSecurePolicy.Always)]
    public async Task The_session_cookie_is_only_pinned_to_https_when_the_deployment_is(
        string authority,
        CookieSecurePolicy expected)
    {
        await using var host = await SignInHost.StartAsync(authority);

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        // `Always` over plain HTTP would strand the signed-in user in a login loop:
        // the ticket cookie is written and then dropped on every callback.
        Assert.Equal(expected, options.Cookie.SecurePolicy);
    }
}
