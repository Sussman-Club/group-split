using System.CommandLine;
using System.Diagnostics;
using GroupSplit.Cli.Auth;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

public static class AuthCommands
{
    private static readonly Option<bool> NoBrowser = new("--no-browser")
    {
        Description = "Print the verification URL instead of opening a browser."
    };

    public static Command Build()
    {
        var auth = new Command("auth", "Sign in to a GroupSplit server and inspect the current session.");

        auth.Subcommands.Add(Login());
        auth.Subcommands.Add(Logout());
        auth.Subcommands.Add(Status());
        auth.Subcommands.Add(Token());

        return auth;
    }

    private static Command Login()
    {
        var command = new Command("login", "Sign in using the OAuth 2.0 device flow.")
        {
            NoBrowser
        };

        command.SetHandler(async (context, ct) =>
        {
            var endpoints = context.Endpoints;
            var output = context.Output;

            var oidc = await context.Discovery.GetAsync(endpoints.Authority, ct);
            var device = await context.DeviceFlow.StartAsync(oidc, endpoints.ClientId, ct);

            var url = device.VerificationUriComplete ?? device.VerificationUri;

            output.Note($"Sign in at: {url}");
            output.Note($"Your code:  {device.UserCode}");

            // Only offer to open a browser when there is a person watching. A build agent
            // that spawns xdg-open has nowhere to show it and may hang holding the handle.
            if (context.ParseResult.GetValue(NoBrowser) || !TryOpenBrowser(url, context))
            {
                output.Note("Waiting for approval...");
            }

            var token = await context.DeviceFlow.PollAsync(oidc, endpoints.ClientId, device, ct);
            var credential = TokenProvider.Persist(token);

            context.Tokens.Save(endpoints.Authority, endpoints.ClientId, credential);

            output.Write(
                new
                {
                    status = "signed_in",
                    username = credential.Username,
                    server = endpoints.Api.ToString(),
                    profile = endpoints.ProfileName,
                    expiresAt = credential.ExpiresAt
                },
                value => new Markup(
                    $"[green]Signed in[/] as [bold]{Markup.Escape(value.username ?? "unknown")}[/] "
                    + $"on profile [bold]{Markup.Escape(value.profile)}[/].\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Logout()
    {
        var command = new Command("logout", "Forget the stored credentials for this server.");

        command.SetHandler(async (context, ct) =>
        {
            var endpoints = context.Endpoints;
            var credential = context.Tokens.Get(endpoints.Authority, endpoints.ClientId);

            context.Tokens.Remove(endpoints.Authority, endpoints.ClientId);

            // Local state is gone either way; ending the Keycloak session is best effort
            // so an unreachable server cannot leave the machine still logged in.
            if (credential?.RefreshToken is { } refreshToken)
            {
                var oidc = await context.Discovery.GetAsync(endpoints.Authority, ct);
                await context.DeviceFlow.RevokeAsync(oidc, endpoints.ClientId, refreshToken, ct);
            }

            context.Output.Write(
                new { status = "signed_out", profile = endpoints.ProfileName },
                _ => new Markup("[green]Signed out.[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Status()
    {
        var command = new Command("status", "Show who you are signed in as, and against which server.");

        command.SetHandler((context, _) =>
        {
            var endpoints = context.Endpoints;
            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariables.Token);

            // Reading stored credentials needs an authority to key on; with a token from
            // the environment there is nothing to look up, and status must still answer.
            var credential = endpoints.AuthorityOrNull is { } authority
                ? context.Tokens.Get(authority, endpoints.ClientId)
                : null;

            var status = fromEnvironment is not null
                ? "token_from_environment"
                : credential is null
                    ? "signed_out"
                    : credential.IsExpired
                        ? "expired"
                        : "signed_in";

            var payload = new
            {
                status,
                username = fromEnvironment is not null
                    ? JwtClaims.Read(fromEnvironment).Username
                    : credential?.Username,
                profile = endpoints.ProfileName,
                api = endpoints.Api.ToString(),
                authority = endpoints.AuthorityOrNull?.ToString(),
                clientId = endpoints.ClientId,
                expiresAt = credential?.ExpiresAt,
                source = fromEnvironment is not null ? EnvironmentVariables.Token : context.Tokens.Path
            };

            context.Output.Write(payload, value =>
            {
                var table = KeyValueTable();
                table.AddRow("Status", value.status);
                table.AddRow("User", value.username ?? "-");
                table.AddRow("Profile", value.profile);
                table.AddRow("API", value.api);
                table.AddRow("Authority", value.authority ?? "-");
                table.AddRow("Client", value.clientId);
                table.AddRow("Expires", value.expiresAt?.ToLocalTime().ToString("g") ?? "-");
                table.AddRow("Credentials", value.source);

                return table;
            });

            // Deliberately not an error exit: "am I signed in" is a question, and a caller
            // asking it should get an answer rather than have to catch a failure.
            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static Command Token()
    {
        var command = new Command("token",
            "Print the current access token, for piping into other tools. Treat it as a secret.");

        command.SetHandler(async (context, ct) =>
        {
            var provider = new TokenProvider(
                context.Tokens, context.DeviceFlow, context.Discovery, context.Endpoints);

            var token = await provider.GetAccessTokenAsync(ct);

            // Raw on stdout even in text mode: the whole point is `--header "Authorization:
            // Bearer $(groupsplit auth token)"`, and a table would break that.
            context.RawOutput.WriteLine(token);

            return ExitCodes.Success;
        });

        return command;
    }

    private static Table KeyValueTable() =>
        new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("key").PadRight(2))
            .AddColumn("value");

    private static bool TryOpenBrowser(string url, CliContext context)
    {
        if (!context.Output.Settings.Interactive)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            context.Output.Note("Opened your browser. Waiting for approval...");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // No desktop session, or nothing registered for http. Not worth failing over:
            // the URL is already on screen and can be opened anywhere.
            return false;
        }
    }
}
