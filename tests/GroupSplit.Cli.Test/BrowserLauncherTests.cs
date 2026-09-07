using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The guard on the one path in this CLI that ends in ShellExecute. The string it checks
/// arrives in the identity server's device authorization response, so it is attacker-
/// controlled the moment that server is hostile or the connection to it is not.
/// </summary>
public sealed class BrowserLauncherTests
{
    [Theory]
    [InlineData("https://example.com/device")]
    [InlineData("https://example.com/device?user_code=ABCD-EFGH")]
    [InlineData("http://localhost:8080/realms/group-split/device")]
    public void Http_and_https_urls_are_opened(string url)
    {
        Assert.True(BrowserLauncher.TryResolve(url, out var target));
        Assert.Equal(url, target!.ToString());
    }

    [Theory]
    [InlineData("calc.exe")]                        // executed by ShellExecute on Windows
    [InlineData("/usr/bin/xterm")]                  // opened or run by xdg-open
    [InlineData(@"\\attacker\share\payload.exe")]   // UNC path
    [InlineData("file:///etc/passwd")]
    [InlineData("vscode://file/etc/passwd")]        // any registered protocol handler
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_an_http_url_is_refused(string? url)
    {
        Assert.False(BrowserLauncher.TryResolve(url, out var target));
        Assert.Null(target);
    }
}
