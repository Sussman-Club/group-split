using System.ComponentModel;
using System.Diagnostics;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Opens a URL in whatever the desktop uses.
/// <para>
/// Separated from the command mostly so <see cref="TryResolve"/> can be tested on its own:
/// it guards a path that ends in ShellExecute, and a guard nothing exercises is a guard
/// nobody notices the loss of.
/// </para>
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// Accepts only an absolute http or https URL.
    /// <para>
    /// <see cref="ProcessStartInfo.UseShellExecute"/> hands its argument to ShellExecute on
    /// Windows or xdg-open on Linux, and neither requires a URL: an executable path, a UNC
    /// path or a file:// address would be run or opened. The string reaching here came from
    /// the identity server's device authorization response, so it is checked rather than
    /// trusted for being a URL nearly every time.
    /// </para>
    /// </summary>
    public static bool TryResolve(string? url, out Uri? target)
    {
        target = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        target = parsed;
        return true;
    }

    /// <summary>Opens <paramref name="target"/>, reporting whether a browser actually took it.</summary>
    public static bool TryOpen(Uri target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target.ToString()) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            // No desktop session, or nothing registered for http. Not worth failing over:
            // the URL is already on screen and can be opened anywhere.
            return false;
        }
    }
}
