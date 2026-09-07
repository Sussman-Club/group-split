using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The banks a person has linked.
/// </summary>
/// <remarks>
/// Named <c>bank</c> rather than <c>plaid</c> for the same reason the endpoints are: the
/// provider travels as a field, and a command named after the first one would have to be
/// renamed for the second.
/// <para>
/// Linking is two commands rather than one because the middle step happens in the
/// provider's own UI, in a browser, and cannot happen here -- see the note on
/// <see cref="Link"/>.
/// </para>
/// </remarks>
public static class BankCommands
{
    private static readonly Argument<Guid> ConnectionId = new("connection-id")
    {
        Description = "The connection's id, as shown by `groupsplit bank list`."
    };

    public static Command Build()
    {
        var bank = new Command("bank", "Linked banks: what is connected, syncing it, and unlinking.");

        bank.Subcommands.Add(List());
        bank.Subcommands.Add(LinkToken());
        bank.Subcommands.Add(Link());
        bank.Subcommands.Add(Sync());
        bank.Subcommands.Add(Unlink());

        return bank;
    }

    private static Command List()
    {
        var command = new Command("list", "List the banks you have linked, and their accounts.");

        command.SetHandler(async (context, ct) =>
        {
            var connections = await new Api.BankConnectionsClient(context.ApiHttpClient)
                .GetBankConnectionsAsync(ct);

            context.Output.Write(connections, Render);

            return ExitCodes.Success;
        });

        return command;
    }

    private static IRenderable Render(BankConnectionsResponse response)
    {
        // Said outright rather than shown as an empty list: "no banks linked" and "this
        // deployment has no provider configured" are different answers, and only one of
        // them is worth trying to fix by linking one.
        if (!response.Enabled)
        {
            return new Markup("[grey]Bank sync is not configured on this server.[/]\n");
        }

        if (response.Connections.Count == 0)
        {
            return new Markup(Tables.Empty("linked banks") + "\n");
        }

        var table = Tables.Grid("Id", "Institution", "Provider", "Status", "Accounts", "Last synced");

        foreach (var connection in response.Connections)
        {
            table.AddRow(
                connection.Id.ToString(),
                Markup.Escape(connection.InstitutionName),
                Markup.Escape(connection.Provider),
                Status(connection),
                connection.Accounts.Count.ToString(),
                connection.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-");
        }

        var accounts = response.Connections.SelectMany(
            connection => connection.Accounts.Select(account => (connection, account))).ToList();

        if (accounts.Count == 0)
        {
            return table;
        }

        var detail = Tables.Grid("Account", "Institution", "Number", "Type", "Currency");

        foreach (var (connection, account) in accounts)
        {
            detail.AddRow(
                Markup.Escape(account.Name),
                Markup.Escape(connection.InstitutionName),
                account.Mask is null ? "-" : Markup.Escape($"···{account.Mask}"),
                Markup.Escape(account.Subtype is null ? account.Type : $"{account.Type}/{account.Subtype}"),
                Markup.Escape(account.Currency));
        }

        return new Rows(table, new Markup("\n[bold]Accounts[/]\n"), detail);
    }

    /// <summary>
    /// Colour carries the one thing that needs acting on, and the word carries it too, so a
    /// redirected listing loses nothing.
    /// </summary>
    private static string Status(BankConnectionResponse connection) => connection.Status switch
    {
        BankConnectionState.Active => "active",
        BankConnectionState.LoginRequired => "[yellow]login required[/]",
        _ => "[red]revoked[/]"
    };

    private static Command LinkToken()
    {
        var connection = new Option<Guid?>("--connection")
        {
            Description = "Repair this connection instead of linking a new bank."
        };

        var command = new Command(
            "link-token",
            "Ask for a token that opens the provider's linking UI in a browser.")
        {
            connection
        };

        command.SetHandler(async (context, ct) =>
        {
            var token = await new Api.BankConnectionsClient(context.ApiHttpClient).CreateLinkTokenAsync(
                new LinkTokenRequest { ConnectionId = context.ParseResult.GetValue(connection) }, ct);

            context.Output.Write(token, value =>
            {
                var table = Tables.KeyValue();
                table.AddRow("Token", Markup.Escape(value.Token));
                table.AddRow("Expires", value.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

                return new Rows(
                    table,
                    new Markup(
                        "\n[grey]Open the provider's linking UI with this token, then pass what it "
                        + "hands back to:[/] groupsplit bank link <public-token>\n"));
            });

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// The second half of linking. The first half is the provider's own UI, which needs a
    /// browser and a JavaScript SDK, so it cannot happen in a terminal; this exchanges what
    /// that UI hands back for a lasting connection, which is the part a script can do.
    /// </summary>
    private static Command Link()
    {
        var publicToken = new Argument<string>("public-token")
        {
            Description = "The one-time token the provider's linking UI returned."
        };

        var command = new Command("link", "Finish linking a bank with the token its UI returned.")
        {
            publicToken
        };

        command.SetHandler(async (context, ct) =>
        {
            var connection = await new Api.BankConnectionsClient(context.ApiHttpClient).LinkBankConnectionAsync(
                new CreateBankConnectionRequest { PublicToken = context.ParseResult.GetValue(publicToken)! },
                ct);

            context.Output.Write(connection, value => new Markup(
                $"[green]Linked[/] {Markup.Escape(value.InstitutionName)} "
                + $"({value.Accounts.Count} accounts) [grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Sync()
    {
        var command = new Command("sync", "Ask for a fresh pull of transactions from a bank.")
        {
            ConnectionId
        };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(ConnectionId);

            await new Api.BankConnectionsClient(context.ApiHttpClient).SyncBankConnectionAsync(id, ct);

            // Queued, not done: the endpoint answers 202 because a full history can take a
            // minute, so a caller polling `groupsplit inbox summary` is the way to see it
            // arrive rather than waiting on this.
            context.Output.Write(
                new { status = "queued", connectionId = id },
                _ => new Markup(
                    "[green]Sync queued.[/] [grey]New rows appear in: groupsplit inbox list[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Unlink()
    {
        var command = new Command("unlink", "Unlink a bank and stop syncing it.") { ConnectionId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(ConnectionId);
            var client = new Api.BankConnectionsClient(context.ApiHttpClient);

            // Read first so the confirmation names the bank. There is no endpoint for one
            // connection, so this is the listing filtered -- and a guid that is not in it
            // is reported here rather than by a DELETE that would 404 after the prompt.
            var connections = await client.GetBankConnectionsAsync(ct);
            var connection = connections.Connections.FirstOrDefault(candidate => candidate.Id == id);

            if (connection is null)
            {
                throw new CliException(
                    new CliError(
                        "No linked bank with that id.",
                        Shared.Errors.ErrorCodes.NotFound,
                        "List what you have linked with: groupsplit bank list"),
                    ExitCodes.InvalidInput);
            }

            Confirmation.Require(
                context,
                action: "bank.unlink",
                summary: $"Unlink {connection.InstitutionName}?",
                changes:
                [
                    $"{connection.InstitutionName} stops syncing "
                    + $"({connection.Accounts.Count} accounts).",
                    "Rows already filed as expenses are kept; anything still waiting in the inbox goes."
                ],
                confirmCommand: $"groupsplit bank unlink {id} --yes");

            await client.UnlinkBankConnectionAsync(id, ct);

            context.Output.Write(
                new { status = "unlinked", connectionId = id, institution = connection.InstitutionName },
                value => new Markup($"[green]Unlinked[/] {Markup.Escape(value.institution)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
