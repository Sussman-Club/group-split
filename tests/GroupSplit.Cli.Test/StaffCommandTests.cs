using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

[Collection(EnvironmentCollection.Name)]
public sealed class StaffCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();
    public StaffCommandTests() { _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress); _environment.Set(EnvironmentVariables.Token, "test-token"); }
    public void Dispose() { _api.Dispose(); _environment.Dispose(); }

    [Fact]
    public async Task Ticket_filters_are_sent_to_the_staff_endpoint()
    {
        _api.Returns("/api/staff/tickets", new { items = Array.Empty<object>(), hasMore = false });
        var result = await Cli.RunAsync("staff", "tickets", "list", "--type", "Bug", "--status", "New", "--page", "2");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var request = Assert.Single(_api.Requests);
        Assert.Equal("Bug", request.Parameter("type")); Assert.Equal("New", request.Parameter("status")); Assert.Equal("2", request.Parameter("page"));
    }

    [Fact]
    public async Task User_directory_and_activity_are_bounded_by_the_server()
    {
        _api.Returns("/api/staff/users/directory", new { items = Array.Empty<object>(), hasMore = true });
        var result = await Cli.RunAsync("staff", "users", "list", "--page", "1");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(result.Json.GetProperty("hasMore").GetBoolean());
        Assert.Equal("1", Assert.Single(_api.Requests).Parameter("page"));
    }

    [Fact]
    public async Task Reporter_messages_use_the_owner_scoped_endpoint()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/support-reports/{id}/messages", Array.Empty<object>());
        var result = await Cli.RunAsync("support", "messages", id.ToString(), "--page", "1");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("1", Assert.Single(_api.Requests).Parameter("page"));
    }

    [Fact]
    public async Task Forbidden_staff_access_keeps_the_api_error_code()
    {
        _api.Problem("/api/staff/team", 403, "FORBIDDEN", "Staff access is required.");
        var result = await Cli.RunAsync("staff", "team");
        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal("FORBIDDEN", result.Error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Merge_requires_confirmation_after_checking_both_versions()
    {
        var source = Guid.NewGuid(); var target = Guid.NewGuid(); var sourceVersion = Guid.NewGuid(); var targetVersion = Guid.NewGuid();
        object Merchant(Guid id, string name, Guid version) => new { id, name, version, logoUrl = (string?)null, firstSeenAt = DateTimeOffset.UtcNow, transactionCount = 0 };
        _api.Returns($"/api/staff/merchants/{source}/merge-preview", new { source = Merchant(source, "Old shop", sourceVersion), target = Merchant(target, "New shop", targetVersion), transactions = 4, bankRows = 2, aliases = 1 });
        var result = await Cli.RunAsync("staff", "merchants", "merge", source.ToString(), "--target", target.ToString(), "--source-version", sourceVersion.ToString(), "--target-version", targetVersion.ToString());
        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("--transactions", result.Json.GetProperty("confirmCommand").GetString());
        Assert.Contains("--bank-rows", result.Json.GetProperty("confirmCommand").GetString());
        Assert.DoesNotContain(_api.Requests, r => r.Method == "POST");
    }
}
