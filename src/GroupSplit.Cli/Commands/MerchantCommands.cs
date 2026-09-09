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
/// week, a group with no bank linked, or a provider that named a place badly and wants
/// correcting.
/// <para>
/// Unlike categories, a merchant belongs to no group -- two groups that both shop at Lidl
/// share the row -- so nothing here takes a <c>--group</c>. That also means a rename is
/// felt everywhere, which is why the listing carries a count and why the destructive
/// commands say so before they run.
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
            Description = "Only merchants whose name contains this."
        };

        var list = new Command("list", "List merchants, alphabetically.") { search };

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

                var table = Tables.Grid("Id", "Name", "Logo", "Used by");

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
        var command = new Command("show", "Show one merchant.") { MerchantId };

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
                table.AddRow("Used by", $"{value.TransactionCount} transaction(s)");

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
            Description = "Image to show for it. Omit for a place that renders as initials."
        };

        var command = new Command("create", "Add a place money gets spent.") { name, logo };

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
    private static Command Update()
    {
        var name = new Option<string?>("--name") { Description = "Rename it." };
        var logo = new Option<string?>("--logo-url") { Description = "Show this image for it." };
        var noLogo = new Option<bool>("--no-logo")
        {
            Description = "Clear the logo, so it renders as initials."
        };

        var command = new Command("update", "Change a merchant's name or its logo.")
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

            // A rename is felt in every group that shops here, so the count is the thing
            // worth saying out loud before it happens.
            if (parse.GetValue(name) is { Length: > 0 } renamed && renamed != current.Name)
            {
                Confirmation.Require(
                    context,
                    action: "merchants.update",
                    summary: $"Rename '{current.Name}' to '{renamed}'?",
                    changes:
                    [
                        $"{current.TransactionCount} transaction(s) point at this merchant, "
                        + "in every group that has spent here -- all of them will show the new name.",
                        "Nothing about how those expenses divide changes."
                    ],
                    confirmCommand: $"groupsplit merchants update {id} --name \"{renamed}\" --yes");
            }

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

    private static Command Delete()
    {
        var command = new Command("delete", "Delete a merchant.") { MerchantId };

        command.SetHandler(async (context, ct) =>
        {
            var id = context.ParseResult.GetValue(MerchantId);
            var client = new Api.MerchantsClient(context.ApiHttpClient);
            var current = await client.GetMerchantAsync(id, ct);

            Confirmation.Require(
                context,
                action: "merchants.delete",
                summary: $"Delete the merchant '{current.Name}'?",
                changes:
                [
                    $"'{current.Name}' stops being offered when an expense is recorded.",
                    "Refused if any transaction still points at it -- including a bank row "
                    + "still waiting in an inbox -- because a merchant cannot take somebody's "
                    + "spending history with it."
                ],
                confirmCommand: $"groupsplit merchants delete {id} --yes");

            await client.DeleteMerchantAsync(id, ct);

            context.Output.Write(
                new { status = "deleted", merchantId = id, name = current.Name },
                value => new Markup($"[green]Deleted[/] {Markup.Escape(value.name)}.\n"));

            return ExitCodes.Success;
        });

        return command;
    }
}
