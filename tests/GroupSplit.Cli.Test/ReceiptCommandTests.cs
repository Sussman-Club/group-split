using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

[Collection(EnvironmentCollection.Name)]
public sealed class ReceiptCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();
    private static readonly Guid Expense = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();
    public ReceiptCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }
    public void Dispose() { _api.Dispose(); _environment.Dispose(); }

    [Fact]
    public async Task Set_sends_rule_version_and_tax_on_each_item()
    {
        _api.Returns($"/api/transactions/{Expense}/receipt", Bill());
        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(), "--item", $"Food=100/tax6@{Version}");
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var body = _api.Requests.Last().Json;
        Assert.Equal(Version, body.GetProperty("items")[0].GetProperty("splitRuleVersionId").GetGuid());
        Assert.Equal(6m, body.GetProperty("items")[0].GetProperty("taxAmount").GetDecimal());
    }
    [Fact]
    public async Task Rule_command_updates_one_item()
    {
        var item = Guid.NewGuid();
        _api.Returns($"/api/transactions/{Expense}/receipt/items/{item}/rule", Bill());
        var result = await Cli.RunAsync("receipts", "rule", Expense.ToString(), item.ToString(), "--rule-version", Version.ToString());
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(Version, _api.Requests.Last().Json.GetProperty("splitRuleVersionId").GetGuid());
    }
    [Fact]
    public void Parser_keeps_item_identity_when_correcting_a_line()
    {
        var id = Guid.NewGuid();
        var item = ReceiptItems.Parse("--item", [$"{id}#Food=20x2/tax1@{Version}"]).Single();
        Assert.Equal(id, item.Id); Assert.Equal(Version, item.SplitRuleVersionId);
        Assert.Equal(2, item.Quantity); Assert.Equal(20m, item.TotalPrice); Assert.Equal(1m, item.TaxAmount);
    }
    [Theory]
    [InlineData("Food=10@not-a-version")]
    [InlineData("Food=10/tax")]
    public async Task Malformed_item_is_refused_before_a_request(string value)
    {
        var result = await Cli.RunAsync("receipts", "set", Expense.ToString(), "--item", value);
        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode); Assert.Empty(_api.Requests);
    }
    [Fact]
    public async Task Removed_bank_split_command_is_not_available()
    {
        var result = await Cli.RunAsync("receipts", "split", Expense.ToString());
        Assert.NotEqual(ExitCodes.Success, result.ExitCode); Assert.Empty(_api.Requests);
    }
    private static object Bill() => new { id = Guid.NewGuid(), expenseId = Expense, subtotal = 100m,
        tax = 6m, tip = 0m, total = 106m, missingRuleItemCount = 0, canDivide = true, dividesItsExpense = true,
        items = Array.Empty<object>() };
}
