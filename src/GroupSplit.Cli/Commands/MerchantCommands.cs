using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using GroupSplit.Shared;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// The places money gets spent, and the marks that stand for them.
/// </summary>
/// <remarks>
/// A sync fills this table on its own from what a bank says, so most of the time there is
/// nothing to do here. These commands are the other way in: cash at the same shop every
/// week, or a group with no bank linked.
/// <para>
/// Unlike categories, a merchant belongs to no group -- two groups that both shop at Lidl
/// share the row -- so nothing here takes a <c>--group</c>. That is also why the listing
/// is the caller's own places only, and why <c>update</c> and <c>delete</c> are refused by
/// the server for every account (<c>MERCHANT_EDIT_FORBIDDEN</c>): renaming, relogoing or
/// deleting a shared row changes what other people see on their own expenses. The two
/// commands stay, so the refusal arrives with its code and its reason rather than as an
/// unknown command, and they ask for no confirmation, since nothing they send is carried
/// out.
/// </para>
/// </remarks>
public static class MerchantCommands
{
    private static readonly Argument<Guid> MerchantId = new("merchant-id")
    {
        Description = "The merchant's id, as shown by `groupsplit merchants list`."
    };

    public static Command Build()
    {
        var merchants = new Command("merchants",
            "Places money gets spent, shared across every group, and their logos.");

        merchants.Subcommands.Add(List());
        merchants.Subcommands.Add(Show());
        merchants.Subcommands.Add(Create());
        merchants.Subcommands.Add(Update());
        merchants.Subcommands.Add(Delete());

        return merchants;
    }

    private static Command List()
    {
        var search = new Option<string?>("--search")
        {
            Description = "Only merchants whose name contains this. At least 2 characters."
        };

        var list = new Command("list",
            "List the places you have an expense or an imported bank row at, alphabetically, "
            + "at most 20.") { search };

        list.SetHandler(async (context, ct) =>
        {
            var result = await new Api.MerchantsClient(context.ApiHttpClient)
                .GetMerchantsAsync(context.ParseResult.GetValue(search), ct);

            context.Output.Write(result, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("merchants") + "\n");
                }

                var table = Tables.Grid("Id", "Name", "Logo", "Your transactions");

                foreach (var merchant in value)
                {
                    table.AddRow(
                        merchant.Id.ToString(),
                        Markup.Escape(merchant.Name),
                        // The URL itself, not a yes or no: it is the thing somebody is here
                        // to check when a logo is not rendering.
                        Markup.Escape(merchant.LogoUrl ?? "-"),
                        merchant.TransactionCount.ToString());
                }

                return table;
            });

            return ExitCodes.Success;
        });

        return list;
    }

    private static Command Show()
    {
        var command = new Command("show", "Show one of your merchants.") { MerchantId };

        command.SetHandler(async (context, ct) =>
        {
            var merchant = await new Api.MerchantsClient(context.ApiHttpClient)
                .GetMerchantAsync(context.ParseResult.GetValue(MerchantId), ct);

            context.Output.Write(merchant, value =>
            {
                var table = Tables.KeyValue();
                table.AddRow("Id", value.Id.ToString());
                table.AddRow("Name", Markup.Escape(value.Name));
                table.AddRow("Logo", Markup.Escape(value.LogoUrl ?? "-"));
                table.AddRow("First seen", value.FirstSeenAt.ToLocalTime().ToString("yyyy-MM-dd"));
                table.AddRow("Your transactions", value.TransactionCount.ToString());

                return table;
            });

            return ExitCodes.Success;
        });

        return command;
    }

    private static Command Create()
    {
        var name = new Argument<string>("name") { Description = "What the place is called." };

        var logo = new Option<string?>("--logo-url")
        {
            Description = "Image to show for it, from the bank provider's logo hosts. "
                          + "Omit for a place that renders as initials."
        };

        var command = new Command("create",
            "Add a place money gets spent. One that already exists is handed back as it is.") { name, logo };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;

            var merchant = await new Api.MerchantsClient(context.ApiHttpClient).CreateMerchantAsync(
                new CreateMerchantRequest
                {
                    Name = parse.GetValue(name)!,
                    LogoUrl = parse.GetValue(logo)
                },
                ct);

            context.Output.Write(merchant, value => new Markup(
                $"[green]Created[/] {Markup.Escape(value.Name)} [grey]{value.Id}[/]\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// A whole replacement, because the endpoint is a PUT whose request requires the name.
    /// So anything not given is read back off the merchant first rather than being sent
    /// blank -- an update that says nothing about the logo should not clear it.
    /// </summary>
    /// <remarks>
    /// Refused by the server for every account today; see the class remarks. Nothing here
    /// asks first for that reason.
    /// </remarks>
    private static Command Update()
    {
        var name = new Option<string?>("--name") { Description = "Rename it." };
        var logo = new Option<string?>("--logo-url") { Description = "Show this image for it." };
        var noLogo = new Option<bool>("--no-logo")
        {
            Description = "Clear the logo, so it renders as initials."
        };

        var command = new Command("update",
            "Change a merchant's name or its logo. Refused for every account: merchants are shared.")
        {
            MerchantId, name, logo, noLogo
        };

        command.SetHandler(async (context, ct) =>
        {
            var parse = context.ParseResult;
            var id = parse.GetValue(MerchantId);

            // GetValue for the flag and GetResult for the URL: a bool option always has a
            // result, so asking whether it was given has to mean asking whether it is true.
            if (parse.GetValue(noLogo) && parse.GetResult(logo) is not null)
            {
                throw CliException.Input(
                    "--logo-url and --no-logo contradict each other.",
                    "Pass one or the other.");
            }

            var client = new Api.MerchantsClient(context.ApiHttpClient);
            var current = await client.GetMerchantAsync(id, ct);

            var updated = await client.UpdateMerchantAsync(
                id,
                new UpdateMerchantRequest
                {
                    Name = parse.GetValue(name) ?? current.Name,
                    LogoUrl = parse.GetValue(noLogo) ? null : parse.GetValue(logo) ?? current.LogoUrl
                },
                ct);

            context.Output.Write(updated, value => new Markup(
                $"[green]Updated[/] {Markup.Escape(value.Name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>
    /// Sent as asked, with no read and no confirmation first: the server refuses it for
    /// every account today, so there is nothing to agree to.
    /// </summary>
    private static Command Delete()
    {
        var command = new Command("delete",
            "Delete a merchant. Refused for every account: merchants are shared.") { MerchantId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(MerchantId);

            await new Api.MerchantsClient(context.ApiHttpClient).DeleteMerchantAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", merchantId = id },
                value => new Markup($"[green]Deleted[/] {value.merchantId}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
