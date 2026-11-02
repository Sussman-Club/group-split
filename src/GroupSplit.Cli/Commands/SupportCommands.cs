using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.Cli.Api;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

/// <summary>The current reporter's public support conversations.</summary>
public static class SupportCommands
{
    public static Command Build()
    {
        var root = new Command("support", "View and reply to your support reports.");
        root.Subcommands.Add(Read("list", "List your reports.", "support-reports"));
        root.Subcommands.Add(Read("notifications", "Count unread support replies.", "support-reports/notifications"));
        var id = new Argument<Guid>("id") { Description = "Report ID." };
        var page = new Option<int>("--page") { Description = "Zero-based page of messages." };
        var messages = new Command("messages", "Read public messages on your report.") { id, page };
        messages.SetHandler(async (c, ct) => { await ReadJson(c, $"support-reports/{c.ParseResult.GetValue(id)}/messages?page={c.ParseResult.GetValue(page)}", ct); return ExitCodes.Success; }); root.Subcommands.Add(messages);
        var body = new Argument<string>("body") { Description = "Your public reply." };
        var reply = new Command("reply", "Reply to your report.") { id, body };
        reply.SetHandler(async (c, ct) => { using var response = await c.ApiHttpClient.PostAsJsonAsync($"support-reports/{c.ParseResult.GetValue(id)}/messages", new CreateSupportMessageRequest { Body = c.ParseResult.GetValue(body)! }, ct); await Check(response, ct); var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct); Write(c, data); return ExitCodes.Success; }); root.Subcommands.Add(reply);
        var read = new Command("mark-read", "Mark your report's replies as read.") { id };
        read.SetHandler(async (c, ct) => { using var response = await c.ApiHttpClient.PostAsync($"support-reports/{c.ParseResult.GetValue(id)}/read", null, ct); await Check(response, ct); c.Output.WriteMessage("Marked as read."); return ExitCodes.Success; }); root.Subcommands.Add(read);
        return root;
    }
    private static Command Read(string name, string description, string url) { var command = new Command(name, description); command.SetHandler(async (c, ct) => { await ReadJson(c, url, ct); return ExitCodes.Success; }); return command; }
    private static async Task ReadJson(CliContext context, string url, CancellationToken ct) { using var response = await context.ApiHttpClient.GetAsync(url, ct); await Check(response, ct); Write(context, await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)); }
    private static void Write(CliContext context, JsonElement data) => context.Output.Write(data, value => new Markup(Markup.Escape(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })) + "\n"));
    private static async Task Check(HttpResponseMessage response, CancellationToken ct) { if (response.IsSuccessStatusCode) return; var body = await response.Content.ReadAsStringAsync(ct); var headers = response.Headers.Concat(response.Content.Headers).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => (IEnumerable<string>)x.SelectMany(v => v.Value).ToArray(), StringComparer.OrdinalIgnoreCase); throw new ApiException("Support API request failed.", (int)response.StatusCode, body, headers, null); }
}
