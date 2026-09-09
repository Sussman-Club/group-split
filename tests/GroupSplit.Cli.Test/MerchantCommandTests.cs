using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The merchant commands, driven the way a caller drives them: real command tree, real
/// generated client, real HTTP against a stub.
/// </summary>
/// <remarks>
/// A sync fills this table on its own, so these commands are the by-hand way in -- and the
/// two that change a shared row are the interesting ones. A merchant belongs to no group,
/// so a rename is felt in every group that has spent there, and neither the rename nor the
/// delete may happen without the caller having been told so.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class MerchantCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public MerchantCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    private static object Merchant(Guid id, string name, string? logo = null, int used = 0) => new
    {
        id,
        name,
        logoUrl = logo,
        firstSeenAt = DateTimeOffset.UtcNow,
        transactionCount = used
    };

    [Fact]
    public async Task Listing_shows_what_came_back()
    {
        _api.Returns("/api/merchants", new[] { Merchant(Guid.NewGuid(), "Lidl", "https://logos/lidl.png", 12) });

        var result = await Cli.RunAsync("merchants", "list");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Lidl", result.Json[0].GetProperty("name").GetString());
        Assert.Equal(12, result.Json[0].GetProperty("transactionCount").GetInt32());
    }

    [Fact]
    public async Task A_search_is_sent_rather_than_filtered_here()
    {
        _api.Returns("/api/merchants", Array.Empty<object>());

        await Cli.RunAsync("merchants", "list", "--search", "lid");

        var read = Assert.Single(_api.Requests, request => request.Method == "GET");
        Assert.Equal("lid", read.Parameter("search"));
    }

    [Fact]
    public async Task Creating_sends_the_name_and_the_logo()
    {
        var id = Guid.NewGuid();
        _api.Returns("/api/merchants", Merchant(id, "Lidl", "https://logos/lidl.png"), method: "POST");

        var result = await Cli.RunAsync(
            "merchants", "create", "Lidl", "--logo-url", "https://logos/lidl.png");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var written = Assert.Single(_api.Requests, request => request.Method == "POST");
        Assert.Equal("Lidl", written.Json.GetProperty("name").GetString());
        Assert.Equal("https://logos/lidl.png", written.Json.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task A_place_with_no_logo_is_created_without_one()
    {
        var id = Guid.NewGuid();
        _api.Returns("/api/merchants", Merchant(id, "Corner shop"), method: "POST");

        var result = await Cli.RunAsync("merchants", "create", "Corner shop");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Asserted on what was sent rather than on what came back: a place with no logo
        // renders as initials, and the request is where the CLI could have invented one.
        var written = Assert.Single(_api.Requests, request => request.Method == "POST");
        Assert.Equal("Corner shop", written.Json.GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, written.Json.GetProperty("logoUrl").ValueKind);
    }

    /// <summary>
    /// A rename reaches every group that has spent here, so it is gated like a delete is --
    /// and the prompt has to carry the count, which is the only thing that says whether this
    /// is a tidy-up or a rewrite of somebody's history.
    /// </summary>
    [Fact]
    public async Task Renaming_asks_first_and_says_how_much_it_reaches()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "LIDL GmbH", used: 412));

        var result = await Cli.RunAsync("merchants", "update", id.ToString(), "--name", "Lidl");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);

        var envelope = result.Json;
        Assert.Equal("merchants.update", envelope.GetProperty("action").GetString());

        var changes = envelope.GetProperty("changes").EnumerateArray()
            .Select(change => change.GetString() ?? "")
            .ToList();

        Assert.Contains(changes, change => change.Contains("412"));

        Assert.DoesNotContain(_api.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task Renaming_goes_through_once_confirmed_and_keeps_the_logo_it_had()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "LIDL GmbH", "https://logos/lidl.png", 412));

        var result = await Cli.RunAsync(
            "merchants", "update", id.ToString(), "--name", "Lidl", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // A PUT requires the whole row, so anything not named is read back off the merchant
        // first: an update about the name must not clear the logo on its way past.
        var written = Assert.Single(_api.Requests, request => request.Method == "PUT");
        Assert.Equal("Lidl", written.Json.GetProperty("name").GetString());
        Assert.Equal("https://logos/lidl.png", written.Json.GetProperty("logoUrl").GetString());
    }

    /// <summary>
    /// Only the name is shared enough to gate. Setting a logo on a place that had none is
    /// not something anybody needs to be talked out of.
    /// </summary>
    [Fact]
    public async Task Setting_only_the_logo_needs_no_confirmation()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl", used: 12));

        var result = await Cli.RunAsync(
            "merchants", "update", id.ToString(), "--logo-url", "https://logos/lidl.png");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var written = Assert.Single(_api.Requests, request => request.Method == "PUT");
        Assert.Equal("Lidl", written.Json.GetProperty("name").GetString());
        Assert.Equal("https://logos/lidl.png", written.Json.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task Clearing_the_logo_sends_null_rather_than_the_one_it_had()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl", "https://logos/lidl.png", 12));

        var result = await Cli.RunAsync("merchants", "update", id.ToString(), "--no-logo");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var written = Assert.Single(_api.Requests, request => request.Method == "PUT");
        Assert.Null(written.Json.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task The_two_logo_flags_contradict_each_other()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl"));

        var result = await Cli.RunAsync(
            "merchants", "update", id.ToString(), "--logo-url", "https://logos/lidl.png", "--no-logo");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);

        // Refused before anything was written, not after.
        Assert.DoesNotContain(_api.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task Deleting_asks_first_and_names_what_it_would_take()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl", used: 12));

        var result = await Cli.RunAsync("merchants", "delete", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("merchants.delete", result.Json.GetProperty("action").GetString());
        Assert.DoesNotContain(_api.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Deleting_goes_through_once_confirmed()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl"));

        var result = await Cli.RunAsync("merchants", "delete", id.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_api.Requests, request => request.Method == "DELETE");
    }

    // ---- naming a shop when recording an expense -------------------------------------

    [Fact]
    public async Task Recording_an_expense_can_say_where_it_was_spent()
    {
        var merchant = Guid.NewGuid();
        _api.Returns("/api/transactions", new { id = Guid.NewGuid(), name = "Weekly shop", amount = 40m },
            method: "POST");

        var result = await Cli.RunAsync(
            "transactions", "create", "Weekly shop", "40", "--merchant-id", merchant.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var written = Assert.Single(_api.Requests, request => request.Method == "POST");
        Assert.Equal(merchant, written.Json.GetProperty("merchantId").GetGuid());
    }

    [Fact]
    public async Task An_expense_can_be_told_where_it_was_spent_afterwards()
    {
        var id = Guid.NewGuid();
        var merchant = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", new { id, name = "Weekly shop", amount = 40m });

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--merchant-id", merchant.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var patch = Assert.Single(_api.Requests, request => request.Method == "PATCH");
        var operation = patch.Json.EnumerateArray().Single();

        Assert.Equal("replace", operation.GetProperty("op").GetString());
        Assert.Equal("/merchantId", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(merchant, operation.GetProperty("value").GetGuid());
    }

    [Fact]
    public async Task An_expense_can_forget_where_it_was_spent()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", new { id, name = "Weekly shop", amount = 40m });

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--no-merchant");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var patch = Assert.Single(_api.Requests, request => request.Method == "PATCH");
        var operation = patch.Json.EnumerateArray().Single();

        Assert.Equal("/merchantId", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, operation.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task The_two_merchant_flags_contradict_each_other()
    {
        var id = Guid.NewGuid();

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(),
            "--merchant-id", Guid.NewGuid().ToString(), "--no-merchant");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.DoesNotContain(_api.Requests, request => request.Method == "PATCH");
    }
}
