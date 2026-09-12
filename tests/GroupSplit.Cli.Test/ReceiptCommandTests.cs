using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// Transcribing a bill and splitting the charge behind it, driven the way a caller drives
/// them: real command tree, real generated client, real HTTP against a stub.
/// </summary>
/// <remarks>
/// The two grammars are what these are really about. A line and a part are each written on
/// one line of a shell because typing a whole receipt is the point, and a dense grammar is
/// exactly the kind that goes wrong quietly -- a price that did not parse makes the subtotal
/// come out short, and the refusal then blames the arithmetic rather than the typo.
/// <para>
/// So what is pinned here is what was actually sent, not what the command printed.
/// </para>
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class ReceiptCommandTests : IDisposable
{
    private static readonly Guid RowId = Guid.Parse("3f2a1b4c-5d6e-4f70-8a91-b2c3d4e5f642");
    private static readonly Guid GroupId = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");
    private static readonly Guid CategoryId = Guid.Parse("ca7e0090-0000-4000-8000-000000000090");

    private static readonly Guid[] Lines =
    [
        Guid.Parse("11111111-0000-4000-8000-000000000001"),
        Guid.Parse("11111111-0000-4000-8000-000000000002"),
        Guid.Parse("11111111-0000-4000-8000-000000000003"),
        Guid.Parse("11111111-0000-4000-8000-000000000004")
    ];

    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public ReceiptCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    // ---- a line of the bill ------------------------------------------------------------

    /// <summary>
    /// A line says it was not taxed, and that reaches the request.
    /// </summary>
    /// <remarks>
    /// The warehouse bill: groceries exempt, general goods not. Weighing the tax over every
    /// line would tax the bananas and let the jacket off, and nothing downstream can tell the
    /// difference once the flag is lost -- the total still adds up.
    /// </remarks>
    [Fact]
    public async Task A_line_can_say_the_tax_was_not_charged_on_it()
    {
        _api.Returns($"/api/inbox/{RowId}/receipt", Bill());

        var result = await Cli.RunAsync(
            "receipts", "set", RowId.ToString(), "--bank-row",
            "--item", "Bananas=1.99/notax",
            "--item", "Jacket=34.99");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var items = Sent().GetProperty("items");

        Assert.False(items[0].GetProperty("isTaxable").GetBoolean());
        Assert.True(items[1].GetProperty("isTaxable").GetBoolean());
    }

    /// <summary>
    /// The flag reads the same on either side of the claimants.
    /// </summary>
    /// <remarks>
    /// Both orders are natural to type and neither is more correct, so both are accepted --
    /// the alternative is a line that parses as a claim on a user called "notax" and a
    /// refusal that says so.
    /// </remarks>
    [Theory]
    [InlineData("Bread=4.00/notax@even")]
    [InlineData("Bread=4.00@even/notax")]
    public async Task The_tax_flag_can_be_written_either_side_of_the_claimants(string line)
    {
        _api.Returns($"/api/inbox/{RowId}/receipt", Bill());

        var result = await Cli.RunAsync(
            "receipts", "set", RowId.ToString(), "--bank-row",
            "--item", line,
            "--item", "Jacket=34.99");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var first = Sent().GetProperty("items")[0];

        Assert.False(first.GetProperty("isTaxable").GetBoolean());
        Assert.Equal("Evenly", first.GetProperty("split").GetString());
        Assert.Equal(4.00m, first.GetProperty("totalPrice").GetDecimal());
    }

    /// <summary>A flag nobody defined is a typo, and is refused before anything is sent.</summary>
    [Fact]
    public async Task A_flag_that_is_not_one_is_refused_without_asking_the_server()
    {
        var result = await Cli.RunAsync(
            "receipts", "set", RowId.ToString(), "--bank-row",
            "--item", "Bananas=1.99/taxfree");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    // ---- splitting the charge ----------------------------------------------------------

    /// <summary>
    /// Parts are written by where the lines are on the paper, and reach the API as ids.
    /// </summary>
    /// <remarks>
    /// A warehouse bill is twenty lines and nobody is pasting twenty guids, so ranges are the
    /// ordinary way to write this. The translation happens here because only the bill knows
    /// what is at each position -- which is also why the command reads it first.
    /// </remarks>
    [Fact]
    public async Task Parts_name_lines_by_position_and_are_sent_as_ids()
    {
        _api.Returns($"/api/inbox/{RowId}/receipt", Bill());
        _api.Returns($"/api/inbox/{RowId}/split", Split());

        var result = await Cli.RunAsync(
            "receipts", "split", RowId.ToString(),
            "--part", $"Groceries=1-3@{GroupId}/{CategoryId}",
            "--part", "Clothes=4");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var parts = Sent($"/api/inbox/{RowId}/split").GetProperty("parts");

        Assert.Equal(
            [Lines[0], Lines[1], Lines[2]],
            parts[0].GetProperty("itemIds").EnumerateArray().Select(id => id.GetGuid()));

        Assert.Equal(GroupId, parts[0].GetProperty("groupId").GetGuid());
        Assert.Equal(CategoryId, parts[0].GetProperty("categoryId").GetGuid());

        // No @group is the part that stays on your own ledger -- the jacket that is nobody's
        // business but yours, which is the whole reason a charge gets split.
        Assert.Equal(Lines[3], parts[1].GetProperty("itemIds")[0].GetGuid());
        Assert.Equal(JsonValueKind.Null, parts[1].GetProperty("groupId").ValueKind);
    }

    /// <summary>A line may also be named by its own id, for anything wanting to be exact.</summary>
    [Fact]
    public async Task A_part_can_name_a_line_by_its_id()
    {
        _api.Returns($"/api/inbox/{RowId}/receipt", Bill());
        _api.Returns($"/api/inbox/{RowId}/split", Split());

        var result = await Cli.RunAsync(
            "receipts", "split", RowId.ToString(),
            "--part", $"Groceries={Lines[0]},2,3",
            "--part", "Clothes=4");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        Assert.Equal(
            [Lines[0], Lines[1], Lines[2]],
            Sent($"/api/inbox/{RowId}/split").GetProperty("parts")[0]
                .GetProperty("itemIds").EnumerateArray().Select(id => id.GetGuid()));
    }

    /// <summary>
    /// A position that is not on the bill is refused here, naming the number that was typed.
    /// </summary>
    /// <remarks>
    /// The API would refuse it too, and name an id the caller never saw -- by which point the
    /// interesting part, which line of the paper they meant, is gone.
    /// </remarks>
    [Fact]
    public async Task A_line_number_the_bill_does_not_have_is_refused_before_filing()
    {
        _api.Returns($"/api/inbox/{RowId}/receipt", Bill());

        var result = await Cli.RunAsync(
            "receipts", "split", RowId.ToString(),
            "--part", "Groceries=1-3",
            "--part", "Clothes=9");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("no line 9", result.Stderr, StringComparison.OrdinalIgnoreCase);

        // Read, and nothing filed.
        Assert.DoesNotContain(_api.Requests, request => request.Path.EndsWith("/split", StringComparison.Ordinal));
    }

    /// <summary>
    /// One part is not a split, and the refusal says which command does want one.
    /// </summary>
    [Fact]
    public async Task One_part_is_an_ordinary_filing_and_says_so()
    {
        var result = await Cli.RunAsync(
            "receipts", "split", RowId.ToString(),
            "--part", "Everything=1-4");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("inbox file", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_api.Requests);
    }

    /// <summary>
    /// Filing over a suspected duplicate is never defaulted on.
    /// </summary>
    /// <remarks>
    /// A split files several expenses at once, so going ahead over a payment already recorded
    /// is several wrong balances rather than one. The flag is the person saying they were
    /// told.
    /// </remarks>
    [Fact]
    public async Task Going_ahead_over_a_suspected_duplicate_has_to_be_asked_for()
    {
        _api.Returns($"/api/inbox/{RowId}/receipt", Bill());
        _api.Returns($"/api/inbox/{RowId}/split", Split());

        await Cli.RunAsync(
            "receipts", "split", RowId.ToString(),
            "--part", "Groceries=1-3", "--part", "Clothes=4");

        Assert.False(Sent($"/api/inbox/{RowId}/split").GetProperty("fileAnyway").GetBoolean());

        await Cli.RunAsync(
            "receipts", "split", RowId.ToString(),
            "--part", "Groceries=1-3", "--part", "Clothes=4", "--file-anyway");

        Assert.True(Sent($"/api/inbox/{RowId}/split").GetProperty("fileAnyway").GetBoolean());
    }

    // ---- the stub ----------------------------------------------------------------------

    /// <summary>The body of the last request sent to that route.</summary>
    private JsonElement Sent(string? path = null) =>
        _api.Requests.Last(request => path is null || request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Json;

    /// <summary>
    /// A four-line bill on an imported row: three grocery lines and a jacket, which is the
    /// shape every split here is written against.
    /// </summary>
    private static object Bill() => new
    {
        id = Guid.NewGuid(),
        expenseId = (Guid?)null,
        bankTransactionId = RowId,
        subtotal = 100m,
        tax = 0m,
        tip = 0m,
        total = 100m,
        unclaimedItemCount = 0,
        canDivide = false,
        items = Lines.Select((id, at) => new
        {
            id,
            name = at == 3 ? "JACKET" : $"GROCERY {at + 1}",
            unitPrice = 25m,
            quantity = 1m,
            totalPrice = 25m,
            isTaxable = true,
            expenseId = (Guid?)null,
            split = "Evenly",
            claims = Array.Empty<object>()
        })
    };

    private static object Split() => new
    {
        bankTransactionId = RowId,
        charge = 100m,
        parts = new[]
        {
            new { transactionId = Guid.NewGuid(), name = "Groceries", groupId = (Guid?)GroupId, amount = 75m, itemCount = 3 },
            new { transactionId = Guid.NewGuid(), name = "Clothes", groupId = (Guid?)null, amount = 25m, itemCount = 1 }
        }
    };
}
