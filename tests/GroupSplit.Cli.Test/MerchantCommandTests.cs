using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The merchant commands, driven the way a caller drives them: real command tree, real
/// generated client, real HTTP against a stub.
/// </summary>
/// <remarks>
/// A sync fills this table on its own, so these commands are the by-hand way in -- and the
/// two that would change a shared row are the interesting ones. A merchant belongs to no
/// group, so a rename would be felt in every group that has spent there; the server refuses
/// both for every account, and what is pinned here is that the CLI carries that refusal
/// through cleanly.
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
        _api.Returns("/api/merchants", new[] { Merchant(Guid.NewGuid(), "Lidl", "https://plaid-merchant-logos.plaid.com/lidl.png", 12) });

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
        _api.Returns("/api/merchants", Merchant(id, "Lidl", "https://plaid-merchant-logos.plaid.com/lidl.png"), method: "POST");

        var result = await Cli.RunAsync(
            "merchants", "create", "Lidl", "--logo-url", "https://plaid-merchant-logos.plaid.com/lidl.png");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var written = Assert.Single(_api.Requests, request => request.Method == "POST");
        Assert.Equal("Lidl", written.Json.GetProperty("name").GetString());
        Assert.Equal("https://plaid-merchant-logos.plaid.com/lidl.png", written.Json.GetProperty("logoUrl").GetString());
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
    /// The server refuses every rename, relogo and delete of a merchant, so the CLI asks
    /// nobody to confirm one -- it sends it, and the refusal arrives with its code and with
    /// a remediation that does not send the caller off to sign in again.
    /// </summary>
    [Fact]
    public async Task A_rename_is_sent_without_asking_and_the_refusal_says_why()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "LIDL GmbH", used: 412), method: "GET");
        _api.Problem($"/api/merchants/{id}", 403, "MERCHANT_EDIT_FORBIDDEN",
            "A merchant is shared by every group that has spent there.", method: "PUT");

        var result = await Cli.RunAsync("merchants", "update", id.ToString(), "--name", "Lidl");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal("MERCHANT_EDIT_FORBIDDEN", result.Error.GetProperty("code").GetString());

        var remediation = result.Error.GetProperty("remediation").GetString();
        Assert.Contains("transactions update", remediation);
        Assert.DoesNotContain("auth status", remediation);

        // Sent, not stopped at a confirmation nobody could usefully give.
        Assert.Single(_api.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task A_delete_is_sent_without_asking_and_the_refusal_says_why()
    {
        var id = Guid.NewGuid();
        _api.Problem($"/api/merchants/{id}", 403, "MERCHANT_EDIT_FORBIDDEN",
            "A merchant is shared by every group that has spent there.", method: "DELETE");

        var result = await Cli.RunAsync("merchants", "delete", id.ToString());

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal("MERCHANT_EDIT_FORBIDDEN", result.Error.GetProperty("code").GetString());
        Assert.Single(_api.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task An_update_about_the_name_does_not_clear_the_logo_on_its_way_past()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "LIDL GmbH", "https://plaid-merchant-logos.plaid.com/lidl.png", 412));

        await Cli.RunAsync("merchants", "update", id.ToString(), "--name", "Lidl");

        // A PUT requires the whole row, so anything not named is read back off the merchant
        // first: an update about the name must not send the logo as blank.
        var written = Assert.Single(_api.Requests, request => request.Method == "PUT");
        Assert.Equal("Lidl", written.Json.GetProperty("name").GetString());
        Assert.Equal("https://plaid-merchant-logos.plaid.com/lidl.png", written.Json.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task Clearing_the_logo_sends_null_rather_than_the_one_it_had()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl", "https://plaid-merchant-logos.plaid.com/lidl.png", 12));

        await Cli.RunAsync("merchants", "update", id.ToString(), "--no-logo");

        var written = Assert.Single(_api.Requests, request => request.Method == "PUT");
        Assert.Null(written.Json.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task The_two_logo_flags_contradict_each_other()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/merchants/{id}", Merchant(id, "Lidl"));

        var result = await Cli.RunAsync(
            "merchants", "update", id.ToString(), "--logo-url", "https://plaid-merchant-logos.plaid.com/lidl.png", "--no-logo");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);

        // Refused before anything was written, not after.
        Assert.DoesNotContain(_api.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task A_one_letter_search_is_refused_with_a_way_forward()
    {
        _api.Problem("/api/merchants", 400, "MERCHANT_SEARCH_TOO_SHORT",
            "Search for at least 2 characters, or leave the search out.");

        var result = await Cli.RunAsync("merchants", "list", "--search", "l");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("MERCHANT_SEARCH_TOO_SHORT", result.Error.GetProperty("code").GetString());
        Assert.Contains("--search", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task A_logo_off_the_providers_hosts_is_refused_with_a_way_forward()
    {
        _api.Problem("/api/merchants", 400, "MERCHANT_LOGO_NOT_ALLOWED",
            "A merchant's logo has to come from the bank provider's image hosts.", method: "POST");

        var result = await Cli.RunAsync(
            "merchants", "create", "Lidl", "--logo-url", "https://tracker.example.com/pixel.png");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("MERCHANT_LOGO_NOT_ALLOWED", result.Error.GetProperty("code").GetString());
        Assert.Contains("--logo-url", result.Error.GetProperty("remediation").GetString());
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

        // Naming a place it actually has, which is what forgetting one presupposes. The
        // update builds the request from the expense and sends the difference, so a stub
        // with no merchant on it would be asking to clear something already clear -- the
        // case below.
        _api.Returns($"/api/transactions/{id}",
            new { id, name = "Weekly shop", amount = 40m, merchantId = Guid.NewGuid() });

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--no-merchant");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var patch = Assert.Single(_api.Requests, request => request.Method == "PATCH");
        var operation = patch.Json.EnumerateArray().Single();

        Assert.Equal("/merchantId", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, operation.GetProperty("value").ValueKind);
    }

    /// <summary>
    /// Forgetting a place an expense never had succeeds and sends nothing.
    /// </summary>
    /// <remarks>
    /// The update sends the difference rather than whatever was named, so a flag that
    /// moves nothing produces no operation. It still succeeds, which is the half that
    /// matters: a script running the same command twice is not making a mistake the second
    /// time.
    /// </remarks>
    [Fact]
    public async Task Forgetting_a_place_it_never_had_changes_nothing_and_says_so_kindly()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", new { id, name = "Weekly shop", amount = 40m });

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--no-merchant");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var patch = Assert.Single(_api.Requests, request => request.Method == "PATCH");

        Assert.Empty(patch.Json.EnumerateArray());
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
