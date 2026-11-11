using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.Cli.Api;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

/// <summary>Staff operations use the same role-protected endpoints as the staff workspace.</summary>
public static class StaffCommands
{
    public static Command Build()
    {
        var staff = new Command("staff", "Staff support and shared directory operations.");
        staff.Subcommands.Add(Tickets());
        staff.Subcommands.Add(Merchants());
        staff.Subcommands.Add(Users());
        staff.Subcommands.Add(Activity());
        staff.Subcommands.Add(Views());
        staff.Subcommands.Add(Read("team", "List staff available for assignment.", "staff/team"));
        return staff;
    }

    private static Command Tickets()
    {
        var group = new Command("tickets", "Manage support tickets.");
        var list = new Command("list", "List and filter tickets.");
        var search = Text("--search", "Search title and description.");
        var status = Text("--status", "Filter by status.");
        var type = Text("--type", "Filter by type: Bug, Support, or FeatureRequest.");
        var priority = Text("--priority", "Filter by priority.");
        var assignee = Text("--assignee", "Staff ID, me, or unassigned.");
        var sort = Text("--sort", "updated, oldest, or priority.");
        var page = Page();
        var review = new Option<bool>("--needs-review") { Description = "Only tickets needing classification review." };
        foreach (var option in new Option[] { search, status, type, priority, assignee, sort, page, review })
        {
            list.Options.Add(option);
        }

        list.SetHandler(async (c, ct) => { var p = c.ParseResult; var query = Query(("search", p.GetValue(search)), ("status", p.GetValue(status)), ("type", p.GetValue(type)), ("priority", p.GetValue(priority)), ("assignee", p.GetValue(assignee)), ("sort", p.GetValue(sort)), ("needsReview", p.GetValue(review) ? "true" : null), ("page", p.GetValue(page).ToString())); await Show(c, "staff/tickets" + query, ct); return ExitCodes.Success; });
        group.Subcommands.Add(list);
        group.Subcommands.Add(ShowById("show", "Show a ticket.", "staff/tickets"));
        group.Subcommands.Add(ShowById("messages", "Show public replies and internal notes.", "staff/tickets", "/messages"));
        group.Subcommands.Add(TicketMessage("reply", "Send a public reply visible to the reporter.", true));
        group.Subcommands.Add(TicketMessage("note", "Add an internal staff-only note.", false));
        group.Subcommands.Add(TicketUpdate());
        group.Subcommands.Add(BulkTickets());
        return group;
    }

    private static Command TicketMessage(string name, string description, bool isPublic)
    {
        var id = Id();
        var body = new Argument<string>("body") { Description = "Message text." };
        var command = new Command(name, description) { id, body };
        command.SetHandler(async (c, ct) =>
        {
            var ticketId = c.ParseResult.GetValue(id);
            var message = c.ParseResult.GetValue(body)!;
            if (isPublic)
            {
                var current = await Get<SupportTicketResponse>(c, $"staff/tickets/{ticketId}", ct);
                await Send(c, HttpMethod.Post, $"staff/tickets/{ticketId}/reply", new CreateSupportMessageRequest { Body = message, ExpectedUpdatedAt = current.UpdatedAt }, ct);
            }
            else
            {
                await Send(c, HttpMethod.Post, $"staff/tickets/{ticketId}/notes", new CreateSupportNoteRequest(message), ct);
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command TicketUpdate()
    {
        var id = Id();
        var status = Text("--status", "New status.");
        var priority = Text("--priority", "New priority.");
        var type = Text("--type", "New type.");
        var assign = new Option<Guid?>("--assign") { Description = "Assign to a staff user ID." };
        var unassign = new Option<bool>("--unassign") { Description = "Clear the assignment." };
        var review = new Option<bool?>("--needs-review") { Description = "Set whether classification needs review (true or false)." };
        var command = new Command("update", "Update ticket classification, status, priority or assignment.") { id, status, priority, type, assign, unassign, review };
        command.SetHandler(async (c, ct) =>
        {
            var p = c.ParseResult;
            var ticketId = p.GetValue(id);
            var assigned = p.GetValue(assign);
            if (assigned is not null && p.GetValue(unassign))
            {
                throw CliException.Input("Choose --assign or --unassign, not both.");
            }

            var ticket = await Get<SupportTicketResponse>(c, $"staff/tickets/{ticketId}", ct);
            var edit = new UpdateSupportTicketRequest { Type = p.GetValue(type) ?? ticket.Type, Status = p.GetValue(status) ?? ticket.Status, Priority = p.GetValue(priority) ?? ticket.Priority, ClassificationNeedsReview = p.GetValue(review) ?? ticket.ClassificationNeedsReview, ExpectedUpdatedAt = ticket.UpdatedAt, ChangeAssignment = assigned is not null || p.GetValue(unassign), AssignedUserId = assigned };
            await Send(c, HttpMethod.Put, $"staff/tickets/{ticketId}", edit, ct);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BulkTickets()
    {
        var file = new Argument<string>("file") { Description = "JSON file with tickets and expectedUpdatedAt, plus status and/or assignment." };
        var hash = Text("--sha256", "Expected SHA-256 of the reviewed file.");
        var command = new Command("bulk-update", "Update multiple tickets atomically from a reviewed JSON file.") { file, hash };
        command.SetHandler(async (c, ct) =>
        {
            var bytes = await File.ReadAllBytesAsync(c.ParseResult.GetValue(file)!, ct);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            if (c.ParseResult.GetValue(hash) is { } expected && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Input("The update file changed after review.", "Review the file and run the command again.");
            }

            var body = JsonSerializer.Deserialize<BulkTicketUpdateRequest>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw CliException.Input("The bulk update file is empty.");
            Confirmation.Require(c, "staff.tickets.bulk-update", $"Update {body.Tickets.Count} tickets?", ["The server checks every ticket version before applying any changes."], new Dictionary<Option, string> { [hash] = digest });
            await Send(c, HttpMethod.Post, "staff/tickets/bulk", body, ct);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Views()
    {
        var group = new Command("views", "Manage your saved ticket or merchant views.");
        var resource = new Option<string>("--resource") { Required = true, Description = "Tickets or Merchants." };
        var list = new Command("list", "List your saved views.") { resource };
        list.SetHandler(async (c, ct) => { await Show(c, "staff/views" + Query(("resource", c.ParseResult.GetValue(resource))), ct); return ExitCodes.Success; });
        group.Subcommands.Add(list);
        var name = new Argument<string>("name") { Description = "Name of the view." };
        var url = new Option<string>("--url") { Required = true, Description = "Staff list URL and filters." };
        var save = new Command("save", "Save a staff list URL.") { name, resource, url };
        save.SetHandler(async (c, ct) => { await Send(c, HttpMethod.Post, "staff/views", new SaveStaffViewRequest(c.ParseResult.GetValue(name)!, c.ParseResult.GetValue(resource)!, c.ParseResult.GetValue(url)!), ct); return ExitCodes.Success; });
        group.Subcommands.Add(save);
        var id = Id();
        var delete = new Command("delete", "Delete one of your saved views.") { id };
        delete.SetHandler(async (c, ct) => { var viewId = c.ParseResult.GetValue(id); Confirmation.Require(c, "staff.views.delete", "Delete this saved view?", [$"View ID: {viewId}"]); await Delete(c, $"staff/views/{viewId}", ct); return ExitCodes.Success; });
        group.Subcommands.Add(delete);
        return group;
    }

    private static Command Users()
    {
        var group = new Command("users", "Look up support account context.");
        var list = new Command("list", "List active accounts.");
        var search = Text("--search", "Search name or email.");
        var page = Page();
        list.Options.Add(search);
        list.Options.Add(page);
        list.SetHandler(async (c, ct) => { await Show(c, "staff/users/directory" + Query(("search", c.ParseResult.GetValue(search)), ("page", c.ParseResult.GetValue(page).ToString())), ct); return ExitCodes.Success; });
        group.Subcommands.Add(list);
        group.Subcommands.Add(ShowById("show", "Show one account's support context.", "staff/users"));
        return group;
    }

    private static Command Activity()
    {
        var command = new Command("activity", "Read the staff activity history.");
        var resource = Text("--resource", "Tickets, Merchants, or Users.");
        var id = new Option<Guid?>("--id") { Description = "Restrict to a record ID." };
        var page = Page();
        command.Options.Add(resource);
        command.Options.Add(id);
        command.Options.Add(page);
        command.SetHandler(async (c, ct) => { await Show(c, "staff/activity" + Query(("resource", c.ParseResult.GetValue(resource)), ("resourceId", c.ParseResult.GetValue(id)?.ToString()), ("page", c.ParseResult.GetValue(page).ToString())), ct); return ExitCodes.Success; });
        return command;
    }

    private static Command Merchants()
    {
        var group = new Command("merchants", "Maintain the shared merchant directory.");
        var list = new Command("list", "List shared merchants.");
        var search = Text("--search", "Search merchant names and aliases.");
        var sort = Text("--sort", "name, usage, or newest.");
        var page = Page();
        list.Options.Add(search);
        list.Options.Add(sort);
        list.Options.Add(page);
        list.SetHandler(async (c, ct) => { await Show(c, "staff/merchants" + Query(("search", c.ParseResult.GetValue(search)), ("sort", c.ParseResult.GetValue(sort)), ("page", c.ParseResult.GetValue(page).ToString())), ct); return ExitCodes.Success; });
        group.Subcommands.Add(list);
        group.Subcommands.Add(ShowById("show", "Show a shared merchant.", "staff/merchants"));
        group.Subcommands.Add(ShowById("aliases", "List a merchant's aliases.", "staff/merchants", "/aliases"));
        group.Subcommands.Add(MerchantCreate());
        group.Subcommands.Add(MerchantUpdate());
        group.Subcommands.Add(AliasAdd());
        group.Subcommands.Add(AliasRemove());
        group.Subcommands.Add(UploadLogo());
        group.Subcommands.Add(MergePreview());
        group.Subcommands.Add(Merge());
        return group;
    }

    private static Command MerchantCreate()
    {
        var name = new Argument<string>("name") { Description = "Merchant name." };
        var logo = Text("--logo-url", "Existing logo URL.");
        var command = new Command("create", "Create a shared merchant.") { name, logo };
        command.SetHandler(async (c, ct) => { await Send(c, HttpMethod.Post, "staff/merchants", new CreateMerchantRequest { Name = c.ParseResult.GetValue(name)!, LogoUrl = c.ParseResult.GetValue(logo) }, ct); return ExitCodes.Success; });
        return command;
    }
    private static Command MerchantUpdate()
    {
        var id = Id();
        var name = Text("--name", "New name.");
        var logo = Text("--logo-url", "Existing logo URL; pass empty to clear.");
        var command = new Command("update", "Edit a shared merchant's name or logo URL.") { id, name, logo };
        command.SetHandler(async (c, ct) => { var merchantId = c.ParseResult.GetValue(id); var current = await Get<MerchantResponse>(c, $"staff/merchants/{merchantId}", ct); await Send(c, HttpMethod.Put, $"staff/merchants/{merchantId}", new UpdateMerchantRequest { Name = c.ParseResult.GetValue(name) ?? current.Name, LogoUrl = c.ParseResult.GetResult(logo) is null ? current.LogoUrl : c.ParseResult.GetValue(logo), ExpectedVersion = current.Version }, ct); return ExitCodes.Success; });
        return command;
    }
    private static Command AliasAdd()
    {
        var id = Id();
        var name = new Argument<string>("name") { Description = "Name used by bank imports." };
        var command = new Command("add-alias", "Add an import alias.") { id, name };
        command.SetHandler(async (c, ct) => { await Send(c, HttpMethod.Post, $"staff/merchants/{c.ParseResult.GetValue(id)}/aliases", new CreateMerchantAliasRequest(c.ParseResult.GetValue(name)!), ct); return ExitCodes.Success; });
        return command;
    }
    private static Command AliasRemove()
    {
        var id = Id();
        var alias = new Argument<Guid>("alias-id") { Description = "Alias ID." };
        var command = new Command("remove-alias", "Remove an import alias.") { id, alias };
        command.SetHandler(async (c, ct) => { var merchant = c.ParseResult.GetValue(id); var aliasId = c.ParseResult.GetValue(alias); Confirmation.Require(c, "staff.merchants.remove-alias", "Remove this merchant alias?", [$"Merchant: {merchant}", $"Alias: {aliasId}"]); await Delete(c, $"staff/merchants/{merchant}/aliases/{aliasId}", ct); return ExitCodes.Success; });
        return command;
    }
    private static Command UploadLogo()
    {
        var id = Id();
        var file = new Argument<string>("file") { Description = "PNG image, at most 1 MiB and 512×512 pixels." };
        var command = new Command("upload-logo", "Upload a PNG logo for a shared merchant.") { id, file };
        command.SetHandler(async (c, ct) =>
        {
            var merchant = await Get<MerchantResponse>(c, $"staff/merchants/{c.ParseResult.GetValue(id)}", ct);
            var path = c.ParseResult.GetValue(file)!;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MerchantLogos.MaximumUploadBytes)
            {
                throw CliException.Input("Choose a PNG file up to 1 MiB.");
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(merchant.Id.ToString()), "merchantId");
            form.Add(new StringContent(merchant.Name), "name");
            form.Add(new StringContent(merchant.Version.ToString()), "expectedVersion");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var image = new ByteArrayContent(bytes);
            image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(image, "file", "logo.png");
            using var response = await c.ApiHttpClient.PostAsync("staff/merchant-images", form, ct);
            await Check(response, ct);
            Write(c, await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct));
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command MergePreview()
    {
        var id = Id();
        var target = new Option<Guid>("--target") { Required = true, Description = "Merchant to keep." };
        var command = new Command("merge-preview", "Review the counts and versions before merging.") { id, target };
        command.SetHandler(async (c, ct) => { await Show(c, $"staff/merchants/{c.ParseResult.GetValue(id)}/merge-preview?targetId={c.ParseResult.GetValue(target)}", ct); return ExitCodes.Success; });
        return command;
    }
    private static Command Merge()
    {
        var id = Id();
        var target = new Option<Guid>("--target") { Required = true, Description = "Merchant to keep." };
        var sourceVersion = new Option<Guid>("--source-version") { Required = true, Description = "Source version from merge-preview." };
        var targetVersion = new Option<Guid>("--target-version") { Required = true, Description = "Target version from merge-preview." };
        var transactions = new Option<int?>("--transactions") { Description = "Expected number of expenses from the reviewed preview." };
        var bankRows = new Option<int?>("--bank-rows") { Description = "Expected number of bank rows." };
        var aliases = new Option<int?>("--aliases") { Description = "Expected number of aliases." };
        var command = new Command("merge", "Merge a duplicate after reviewing its preview.") { id, target, sourceVersion, targetVersion, transactions, bankRows, aliases };
        command.SetHandler(async (c, ct) =>
        {
            var p = c.ParseResult;
            var source = p.GetValue(id);
            var destination = p.GetValue(target);
            var preview = await Get<MerchantMergePreview>(c, $"staff/merchants/{source}/merge-preview?targetId={destination}", ct);
            if (preview.Source.Version != p.GetValue(sourceVersion) || preview.Target.Version != p.GetValue(targetVersion))
            {
                throw CliException.Input("Merchants changed since the preview.", "Run merge-preview again and review it.");
            }

            if ((p.GetValue(transactions) is { } t && t != preview.Transactions) || (p.GetValue(bankRows) is { } b && b != preview.BankRows) || (p.GetValue(aliases) is { } a && a != preview.Aliases))
            {
                throw CliException.Input("The merge impact changed after review.", "Run merge-preview again and review it.");
            }

            Confirmation.Require(c, "staff.merchants.merge", $"Merge '{preview.Source.Name}' into '{preview.Target.Name}'?", [$"Move {preview.Transactions} expenses, {preview.BankRows} bank rows, and {preview.Aliases} aliases.", "The destination keeps its name and logo."],
                new Dictionary<Option, string> { [transactions] = preview.Transactions.ToString(), [bankRows] = preview.BankRows.ToString(), [aliases] = preview.Aliases.ToString() });
            await Send(c, HttpMethod.Post, $"staff/merchants/{source}/merge", new MergeMerchantsRequest(destination, p.GetValue(sourceVersion), p.GetValue(targetVersion), preview.Transactions, preview.BankRows, preview.Aliases), ct);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command Read(string name, string description, string url)
    {
        var command = new Command(name, description);
        command.SetHandler(async (c, ct) => { await Show(c, url, ct); return ExitCodes.Success; });
        return command;
    }
    private static Command ShowById(string name, string description, string url, string suffix = "")
    {
        var id = Id();
        var command = new Command(name, description) { id };
        command.SetHandler(async (c, ct) => { await Show(c, $"{url}/{c.ParseResult.GetValue(id)}{suffix}", ct); return ExitCodes.Success; });
        return command;
    }
    private static Argument<Guid> Id() => new("id") { Description = "Record ID." };
    private static Option<string?> Text(string name, string description) => new(name) { Description = description };
    private static Option<int> Page() => new("--page") { Description = "Zero-based page." };
    private static string Query(params (string Key, string? Value)[] values)
    {
        var terms = values.Where(v => v.Value is not null).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}");
        return "?" + string.Join("&", terms);
    }
    private static async Task<T> Get<T>(CliContext context, string url, CancellationToken ct)
    {
        using var response = await context.ApiHttpClient.GetAsync(url, ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }
    private static async Task Show(CliContext context, string url, CancellationToken ct)
    {
        var data = await Get<JsonElement>(context, url, ct);
        Write(context, data);
    }
    private static async Task Send(CliContext context, HttpMethod method, string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, body.GetType()) };
        using var response = await context.ApiHttpClient.SendAsync(request, ct);
        await Check(response, ct);
        if (response.Content.Headers.ContentLength == 0 || response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            context.Output.WriteMessage("Done.");
        }
        else
        {
            Write(context, await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct));
        }
    }
    private static async Task Delete(CliContext context, string url, CancellationToken ct)
    {
        using var response = await context.ApiHttpClient.DeleteAsync(url, ct);
        await Check(response, ct);
        context.Output.WriteMessage("Done.");
    }
    private static void Write(CliContext context, JsonElement data) => context.Output.Write(data, value => new Markup(Markup.Escape(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })) + "\n"));
    private static async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var headers = response.Headers.Concat(response.Content.Headers).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => (IEnumerable<string>)x.SelectMany(v => v.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        throw new ApiException("Staff API request failed.", (int)response.StatusCode, body, headers, null);
    }
}
