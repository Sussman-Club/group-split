using System.Globalization;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The one format every amount on the command line is read in (#313): a <c>.</c> decimal
/// point, no thousands separators, at most two decimal places. The same on every machine.
/// </summary>
/// <remarks>
/// Each case runs under en-US and es-ES. System.CommandLine read a decimal with the
/// machine's culture, so on en-US <c>12,50</c> was 1250 and on es-ES <c>12.50</c> was --
/// a test that ran only under the build machine's culture would have passed on one of them.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class AmountFormatTests : IDisposable
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Expense = Guid.NewGuid();

    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public AmountFormatTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        _api.Dispose();
        _environment.Dispose();
    }

    private static void On(string culture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    }

    public static TheoryData<string, string> Refused => new()
    {
        // A comma is never the decimal point here, so a script cannot mean 12.50 on one
        // machine and 1250 on another.
        { "en-US", "12,50" },
        { "es-ES", "12,50" },
        // Grouping either way round.
        { "en-US", "1.234,56" },
        { "es-ES", "1.234,56" },
        { "en-US", "1,234.56" },
        { "es-ES", "1,234.56" },
        // A third decimal place is not a cent, and is not rounded into one.
        { "en-US", "12.555" },
        { "es-ES", "12.555" }
    };

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public async Task A_point_decimal_is_the_same_amount_on_every_machine(string culture)
    {
        On(culture);
        _api.Returns("/api/transactions", new
        {
            id = Guid.NewGuid(), name = "Cena", description = (string?)null, amount = 12.50m,
            dateTime = DateTimeOffset.UtcNow, groupId = (Guid?)null, groupName = (string?)null,
            paidByUserId = Ana, paidByUserName = "Ana", categoryId = (Guid?)null, category = (string?)null
        });

        var result = await Cli.RunAsync(
            "transactions", "create", "Cena", "12.50",
            "--split", $"{Ana}=6.25", "--split", $"{Omar}=6.25");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(request => request.Method == "POST").Json;

        // Not 1250 on es-ES, where the culture reads the point as grouping.
        Assert.Equal(12.50m, body.GetProperty("amount").GetDecimal());
        Assert.All(body.GetProperty("splits").EnumerateArray(),
            split => Assert.Equal(6.25m, split.GetProperty("amount").GetDecimal()));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task An_expense_amount_in_any_other_format_is_refused_before_a_request(string culture, string amount)
    {
        On(culture);

        var result = await Cli.RunAsync("transactions", "create", "Cena", amount);

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains($"'{amount}'", result.Error.GetProperty("error").GetString());
        Assert.Empty(_api.Requests);
    }

    /// <summary>
    /// The issue's own example: shares misread the same way as the total still add up to
    /// it, so the server's sum check let a $1,250 dinner through. Each share is refused on
    /// its own, whatever the total says.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_share_in_any_other_format_is_refused_before_a_request(string culture, string share)
    {
        On(culture);

        var result = await Cli.RunAsync(
            "transactions", "create", "Cena", "12.50", "--split", $"{Ana}={share}");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains($"--split '{Ana}={share}'", result.Error.GetProperty("error").GetString());
        Assert.Empty(_api.Requests);
    }

    /// <summary>
    /// Every other place an amount is typed, so none of them is left on the old parse.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public async Task Every_amount_option_and_argument_reads_the_same_format(string culture)
    {
        On(culture);
        var group = Guid.NewGuid().ToString();

        string[][] commands =
        [
            ["transactions", "update", Expense.ToString(), "--amount", "12,50"],
            ["groups", "settle", group, Omar.ToString(), "12,50"],
            ["groups", "settle-between", group, Ana.ToString(), Omar.ToString(), "12,50"],
            ["settle", "pay", Omar.ToString(), "--amount", "12,50"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12,50"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12.50/tax1,05"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12.50x1,5"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12.50", "--tip", "1,50"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12.50", "--tax", "1,50"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12.50", "--subtotal", "12,50"],
            ["receipts", "set", Expense.ToString(), "--item", "Wine=12.50", "--total", "12,50"],
            ["split-rules", "create", "Mostly Ana", "--group", group,
                "--percent", $"{Ana}=66,67", "--percent", $"{Omar}=33.33"]
        ];

        foreach (var command in commands)
        {
            var result = await Cli.RunAsync(command);

            Assert.True(ExitCodes.InvalidInput == result.ExitCode,
                $"{string.Join(' ', command)} exited {result.ExitCode}: {result.Stderr}");
            Assert.Contains("'.'", result.Error.GetProperty("error").GetString());
        }

        Assert.Empty(_api.Requests);
    }

    /// <summary>A bill's quantity holds three places, as the bill stores it.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public void A_receipt_quantity_may_have_three_decimal_places(string culture)
    {
        On(culture);

        var line = ReceiptItems.Parse("--item", ["Ham=12.50x1.255"]).Single();

        Assert.Equal(1.255m, line.Quantity);
        Assert.Equal(12.50m, line.TotalPrice);
    }
}
