using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The promise the confirmation envelope makes: run <c>confirmCommand</c> and you get what
/// the prompt described. Checked for every gate by sending the same invocation with
/// <c>--yes</c> first, then re-running the string the prompt handed back -- as a shell would
/// split it, from another directory -- and comparing the writes the server received.
/// </summary>
/// <remarks>
/// Every invocation goes through <c>--profile staging</c>, and only that profile points at
/// the stub: the default one points at a port nobody listens on. A confirmCommand that drops
/// the profile, as every hand-written one did, sends its write nowhere and fails here.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class ConfirmCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();
    private readonly string _originalDirectory = Directory.GetCurrentDirectory();

    public ConfirmCommandTests()
    {
        _environment.Set(EnvironmentVariables.Token, "test-token");

        new ConfigStore().Save(new CliConfigFile
        {
            Profiles =
            {
                ["default"] = new CliProfile { ApiUrl = "http://127.0.0.1:1/api" },
                ["staging"] = new CliProfile { ApiUrl = _api.BaseAddress }
            }
        });
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        _api.Dispose();
        _environment.Dispose();
    }

    // ---- every gate ------------------------------------------------------------------

    /// <summary>One gated invocation: what it is called, what to type, and what to stub.</summary>
    private sealed record Gate(string Action, string[] Args, Action<StubApi> Arrange);

    private static readonly Guid Group = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Daniel = Guid.NewGuid();
    private static readonly Guid Pat = Guid.NewGuid();
    private static readonly Guid Thing = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    /// <summary>
    /// The relative path the history file is named by, spaces and all. Relative to the
    /// directory the prompt ran in, which is not the one the confirmation is re-run from.
    /// </summary>
    private const string HistoryFile = "old rules/history.json";

    private static readonly Gate[] Gates =
    [
        new("bank.unlink", ["bank", "unlink", $"{Thing}"], api =>
            api.Returns("/api/bank-connections", new { enabled = true, connections = new[] { Connection(Thing) } })),

        new("categories.delete", ["categories", "delete", $"{Thing}"], api =>
            api.Returns("/api/categories", new[] { new { id = Thing, groupId = Group, name = "Groceries" } })),

        new("groups.remove-member", ["groups", "remove-member", $"{Group}", $"{Omar}"], api =>
            api.Returns($"/api/groups/{Group}/members", new[] { Member(Omar, "Omar") })),

        // --date, --note and --through were each dropped by the hand-written string at some
        // point; the note carries a quote and a comma for the shell to get wrong.
        new("groups.settle",
        [
            "groups", "settle", $"{Group}", $"{Omar}", "40.50", "--direction", "youpaidthem",
            "--date", "2026-09-30", "--note", "Mum's cash, 50%", "--through", "2026-08-31"
        ], api => api.Returns($"/api/groups/{Group}/members", new[] { Member(Omar, "Omar") })),

        new("groups.settle-between",
        [
            "groups", "settle-between", $"{Group}", $"{Omar}", $"{Daniel}", "43544.61",
            "--date", "2024-08-31", "--note", "Settle up August 2024"
        ], api => api.Returns($"/api/groups/{Group}/members",
            new[] { Member(Omar, "Omar"), Member(Daniel, "Daniel") })),

        // A note that starts like an option: it has to come back attached to --note.
        new("groups.settle-up",
        [
            "groups", "settle-up", $"{Group}", "--date", "2026-09-30", "--note", "-- by transfer",
            "--through", "2026-08-31"
        ], api => api.Returns($"/api/groups/{Group}/balances", new
        {
            netBalances = Array.Empty<object>(),
            owedToYou = new[] { new { userId = Omar, userName = "Omar", amount = 25.00m } },
            youOwed = Array.Empty<object>()
        })),

        new("groups.leave", ["groups", "leave", $"{Group}"], api =>
            api.Returns($"/api/groups/{Group}", AGroup())),

        new("groups.withdraw-invitation", ["groups", "withdraw-invitation", $"{Group}", $"{Thing}"], api =>
            api.Returns($"/api/groups/{Group}/invitations", new[]
            {
                new
                {
                    id = Thing, groupId = Group, groupName = "The flat", name = "Omar", token = "token-omar",
                    invitedByUserName = "Anabel", invitedAt = DateTimeOffset.UtcNow, participantUserId = Omar
                }
            })),

        new("groups.guest-link.create", ["groups", "guest-link", "create", $"{Group}", $"{Pat}", "--new"], api =>
            api.Returns($"/api/groups/{Group}/guests", new[] { Guest("tok-old") }, method: "GET")),

        new("groups.guest-link.revoke", ["groups", "guest-link", "revoke", $"{Group}", $"{Pat}"], api =>
            api.Returns($"/api/groups/{Group}/guests", new[] { Guest("tok-old") }, method: "GET")),

        new("groups.link.create", ["groups", "link", "create", $"{Group}"], api =>
            api.Returns($"/api/groups/{Group}/join-links", new[]
            {
                new
                {
                    id = Thing, groupId = Group, groupName = "The flat", token = "old-token",
                    createdByUserName = "Daniel", createdAt = DateTimeOffset.UtcNow,
                    expiresAt = DateTimeOffset.UtcNow.AddDays(14)
                }
            }, method: "GET")),

        new("groups.link.revoke", ["groups", "link", "revoke", $"{Group}"], api =>
            api.Returns($"/api/groups/{Group}", AGroup())),

        new("invitations.claim", ["invitations", "claim", "token-omar"], api =>
            api.Returns("/api/invitations/claims/token-omar", Claim(), method: "GET")),

        new("invitations.decline", ["invitations", "decline", "token-omar"], api =>
            api.Returns("/api/invitations/claims/token-omar", Claim(), method: "GET")),

        new("inbox.attachments.delete", ["inbox", "attachments", "delete", $"{Thing}", $"{Other}"], api =>
            api.Returns($"/api/inbox/{Thing}/receipt-attachments",
                new[] { Attachment(Other, expenseId: null, bankTransactionId: Thing) }, method: "GET")),

        new("receipts.attachments.delete", ["receipts", "attachments", "delete", $"{Thing}", $"{Other}"], api =>
            api.Returns($"/api/transactions/{Thing}/receipt-attachments",
                new[] { Attachment(Other, expenseId: Thing, bankTransactionId: null) }, method: "GET")),

        new("receipts.divide", ["receipts", "divide", $"{Thing}"], api =>
            api.Returns($"/api/transactions/{Thing}/receipt/preview", new
            {
                receiptId = Other, total = 40m,
                shares = new[] { new { userId = Omar, subtotal = 35m, amount = 40m } }
            }, method: "GET")),

        new("receipts.delete", ["receipts", "delete", $"{Thing}"], api =>
            api.Returns($"/api/transactions/{Thing}/receipt", new
            {
                id = Other, expenseId = Thing, subtotal = 100m, tax = 6m, tip = 0m, total = 106m,
                missingRuleItemCount = 0, canDivide = true, dividesItsExpense = true, canEdit = true,
                items = Array.Empty<object>()
            }, method: "GET")),

        // The logo was the flag the hand-written string left behind.
        new("merchants.update",
        [
            "merchants", "update", $"{Thing}", "--name", "Lidl Plus", "--logo-url", "https://example.com/lidl.png"
        ], api => api.Returns($"/api/merchants/{Thing}", Merchant(), method: "GET")),

        new("merchants.delete", ["merchants", "delete", $"{Thing}"], api =>
            api.Returns($"/api/merchants/{Thing}", Merchant(), method: "GET")),

        new("settle.pay",
        [
            "settle", "pay", $"{Daniel}", "--direction", "youpaidthem",
            "--date", "2026-09-30", "--note", "bank transfer", "--through", "2026-08-31"
        ], api => api.Returns("/api/users/me/settlement-plan", Plan(58.40m))),

        new("split-rules.versions.set", ["split-rules", "versions", "set", $"{Thing}", "--file", HistoryFile], _ => { }),

        new("split-rules.delete", ["split-rules", "delete", $"{Thing}"], api =>
            api.Returns($"/api/split-rules/{Thing}", new
            {
                id = Thing, groupId = Group, name = "By room size",
                versionId = Other, changedAt = DateTimeOffset.UtcNow,
                definition = new Dictionary<string, object>
                {
                    ["$type"] = "shares",
                    ["shares"] = new Dictionary<string, int> { [Omar.ToString()] = 3 }
                }
            }, method: "GET")),

        new("transactions.delete", ["transactions", "delete", $"{Thing}"], api =>
            api.Returns($"/api/transactions/{Thing}", new
            {
                id = Thing, name = "Dinner", description = (string?)null, amount = 42.50m,
                dateTime = DateTimeOffset.UtcNow, groupId = Group, groupName = "The flat",
                paidByUserId = Omar, paidByUserName = "Omar",
                categoryId = (Guid?)null, category = (string?)null
            }, method: "GET")),

        new("users.delete", ["users", "delete"], api =>
            api.Returns("/api/users/me", new
            {
                id = Omar, fullName = "Anabel Benitez", email = "anabel@example.com"
            }, method: "GET"))
    ];

    public static TheoryData<string> Actions => [.. Gates.Select(gate => gate.Action)];

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task Confirming_sends_exactly_what_yes_would_have(string action)
    {
        var gate = Gates.Single(candidate => candidate.Action == action);
        gate.Arrange(_api);

        var here = Scratch();
        var elsewhere = Scratch();
        WriteHistory(here);

        string[] invocation = ["--profile", "staging", .. gate.Args];

        // What the caller would have sent had they said --yes up front.
        Directory.SetCurrentDirectory(here);
        await Cli.RunAsync([.. invocation, "--yes"]);
        var direct = WritesSince(0);

        Assert.NotEmpty(direct);

        // The prompt, which writes nothing.
        var mark = _api.Requests.Count;
        var prompt = await Cli.RunAsync(invocation);

        Assert.Equal(ExitCodes.ConfirmationRequired, prompt.ExitCode);
        Assert.Equal(action, prompt.Json.GetProperty("action").GetString());
        Assert.Empty(WritesSince(mark));

        // And the string it handed back, split as a shell would split it and run from
        // somewhere else.
        var words = PosixShell.Split(prompt.Json.GetProperty("confirmCommand").GetString()!);

        Assert.Equal("groupsplit", words[0]);

        Directory.SetCurrentDirectory(elsewhere);
        mark = _api.Requests.Count;
        await Cli.RunAsync([.. words.Skip(1)]);

        Assert.Equal(direct, WritesSince(mark));
    }

    /// <summary>
    /// The guard on the table above: a gate added to a command and not to the table would
    /// otherwise be a gate nobody checked.
    /// </summary>
    [Fact]
    public void Every_gate_in_the_cli_is_in_the_table()
    {
        var root = Path.GetDirectoryName(AppContext.BaseDirectory)!;
        while (!File.Exists(Path.Combine(root, "group-split.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No repository root above the tests.");
        }

        var gated = Directory
            .EnumerateFiles(Path.Combine(root, "src", "GroupSplit.Cli"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(
                    File.ReadAllText(file),
                    @"Confirmation\.Require\(\s*context,\s*action:\s*""([^""]+)""")
                .Select(match => match.Groups[1].Value))
            .Order()
            .ToList();

        Assert.Equal(gated, Gates.Select(gate => gate.Action).Order().ToList());
    }

    // ---- the parts of it, one at a time -----------------------------------------------

    /// <summary>
    /// The failure the issue opened with: prompted about the staging account, and confirming
    /// deleted the one on the default profile.
    /// </summary>
    [Fact]
    public async Task The_profile_the_prompt_was_about_is_the_one_confirming_acts_on()
    {
        _api.Returns("/api/users/me", new { id = Omar, fullName = "Anabel Benitez", email = "anabel@example.com" });

        var prompt = await Cli.RunAsync("--profile", "staging", "users", "delete");

        Assert.Equal("groupsplit --profile staging users delete --yes",
            prompt.Json.GetProperty("confirmCommand").GetString());
    }

    [Fact]
    public async Task A_server_given_by_flag_is_carried_too()
    {
        // No profile pointing anywhere, so only the flag can find the stub.
        new ConfigStore().Save(new CliConfigFile());

        var origin = _api.BaseAddress[..^"/api".Length];
        _api.Returns("/native/api/users/me", new { id = Omar, fullName = "Anabel Benitez", email = "anabel@example.com" });

        var prompt = await Cli.RunAsync("users", "delete", "--server", origin);
        var words = PosixShell.Split(prompt.Json.GetProperty("confirmCommand").GetString()!);

        await Cli.RunAsync([.. words.Skip(1)]);

        Assert.Contains(_api.Requests, request => request is { Method: "DELETE", Path: "/native/api/users/me" });
    }

    /// <summary>
    /// <c>settle pay</c> without <c>--amount</c> pays whatever is outstanding, and the prompt
    /// names that figure. Confirming after something else moved the balance pays the figure
    /// that was named, not the new remainder.
    /// </summary>
    [Fact]
    public async Task Settle_pay_confirms_the_amount_the_prompt_named()
    {
        _api.Returns("/api/users/me/settlement-plan", Plan(58.40m));

        var prompt = await Cli.RunAsync(
            "--profile", "staging", "settle", "pay", Daniel.ToString(), "--direction", "youpaidthem");

        _api.Returns("/api/users/me/settlement-plan", Plan(18.40m));

        await Cli.RunAsync([.. PosixShell.Split(prompt.Json.GetProperty("confirmCommand").GetString()!).Skip(1)]);

        var paid = _api.Requests.Single(request => request.Path == "/api/users/me/settle").Json;

        Assert.Equal(58.40m, paid.GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// And under a culture that writes a decimal with a comma, since the pinned figure is the
    /// one value that does not come back as the caller typed it.
    /// </summary>
    [Fact]
    public async Task A_pinned_amount_reads_back_the_same_under_a_comma_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");

        try
        {
            await Settle_pay_confirms_the_amount_the_prompt_named();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task The_file_is_named_by_its_absolute_path()
    {
        var here = Scratch();
        WriteHistory(here);
        Directory.SetCurrentDirectory(here);

        var prompt = await Cli.RunAsync(
            "--profile", "staging", "split-rules", "versions", "set", Thing.ToString(), "--file", HistoryFile);

        var words = PosixShell.Split(prompt.Json.GetProperty("confirmCommand").GetString()!);

        Assert.Equal(Path.GetFullPath(Path.Combine(here, HistoryFile)), words[words.IndexOf("--file") + 1]);
    }

    /// <summary>
    /// <c>--fields</c> narrows a result. The envelope is not one, and narrowing it to the
    /// fields of the result the caller expected left an empty object to act on.
    /// </summary>
    [Fact]
    public async Task Fields_does_not_narrow_the_confirmation_envelope()
    {
        _api.Returns($"/api/transactions/{Thing}", new
        {
            id = Thing, name = "Dinner", amount = 42.50m, dateTime = DateTimeOffset.UtcNow,
            groupId = Group, groupName = "The flat", paidByUserId = Omar, paidByUserName = "Omar"
        });

        var prompt = await Cli.RunAsync(
            "--profile", "staging", "transactions", "delete", Thing.ToString(), "--json", "--fields", "id");

        Assert.Equal(ExitCodes.ConfirmationRequired, prompt.ExitCode);
        Assert.True(prompt.Json.GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal("transactions.delete", prompt.Json.GetProperty("action").GetString());
        Assert.NotEmpty(prompt.Json.GetProperty("summary").GetString()!);
        Assert.Equal(
            $"groupsplit --profile staging transactions delete {Thing} --json --fields id --yes",
            prompt.Json.GetProperty("confirmCommand").GetString());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("2026-09-30", "2026-09-30")]
    [InlineData("--note=x", "--note=x")]
    [InlineData("two words", "'two words'")]
    [InlineData("Mum's", @"'Mum'\''s'")]
    [InlineData("", "''")]
    [InlineData("a,b", "'a,b'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData(@"C:\Users\ana\history.json", @"'C:\Users\ana\history.json'")]
    public void Words_are_quoted_only_when_a_shell_would_change_them(string word, string quoted)
    {
        Assert.Equal(quoted, ConfirmCommand.Quote(word));
        Assert.Equal([word], PosixShell.Split(quoted));
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>The requests that change something, as the server saw them.</summary>
    private List<(string Method, string Path, string Query, string Body)> WritesSince(int mark) =>
    [
        .. _api.Requests.Skip(mark)
            .Where(request => request.Method != "GET")
            .Select(request => (request.Method, request.Path, request.Query, request.Body))
    ];

    private static void WriteHistory(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, Path.GetDirectoryName(HistoryFile)!));
        File.WriteAllText(Path.Combine(directory, HistoryFile), """
            [
              { "from": "2023-03-01", "definition": { "$type": "sole" } },
              { "from": "2024-01-01", "definition": { "$type": "sole" } }
            ]
            """);
    }

    private string Scratch() => Directory.CreateDirectory(
        Path.Combine(_environment.Root, Guid.NewGuid().ToString("N"))).FullName;

    private static object Connection(Guid id) => new
    {
        id, provider = "plaid", institutionName = "Monzo", status = "Active",
        linkedAt = DateTimeOffset.UtcNow.AddDays(-30), lastSyncedAt = DateTimeOffset.UtcNow,
        accounts = Array.Empty<object>()
    };

    private static object Member(Guid id, string name) => new
    {
        id, firstName = name, lastName = "Haddad", email = $"{name.ToLowerInvariant()}@example.com"
    };

    private static object AGroup() => new { id = Group, name = "The flat", memberCount = 3, isArchive = false };

    private static object Guest(string token) => new
    {
        userId = Pat, groupId = Group, groupName = "Ski trip", name = "Pat",
        addedByUserName = "Daniel", addedAt = DateTimeOffset.UtcNow,
        hasLink = true, token, linkIssuedByUserName = "Daniel", linkIssuedAt = DateTimeOffset.UtcNow
    };

    private static object Claim() => new
    {
        invitationId = Guid.NewGuid(), groupId = Group, groupName = "The flat", memberCount = 2,
        name = "Omar", invitedByUserName = "Anabel", invitedAt = DateTimeOffset.UtcNow, alreadyAMember = false
    };

    private static object Attachment(Guid id, Guid? expenseId, Guid? bankTransactionId) => new
    {
        id, expenseId, bankTransactionId, receiptId = (Guid?)null,
        fileName = "receipt.pdf", contentType = "application/pdf", length = 3, uploadedAt = DateTimeOffset.UtcNow
    };

    private static object Merchant() => new
    {
        id = Thing, name = "Lidl", logoUrl = (string?)null, firstSeenAt = DateTimeOffset.UtcNow, transactionCount = 12
    };

    private static object Plan(decimal owed) => new
    {
        net = -owed, owedToYouTotal = 0m, youOweTotal = owed,
        youPay = new object[]
        {
            new
            {
                userId = Daniel, userName = "Daniel Rivero", amount = owed,
                groups = new object[] { new { groupId = Group, groupName = "Home", amount = owed } }
            }
        },
        owedToYou = Array.Empty<object>(),
        lastSettled = (DateTimeOffset?)null
    };

    /// <summary>
    /// Word splitting as a POSIX shell does it, for the quoting <c>confirmCommand</c> uses:
    /// single quotes, double quotes and backslashes. No expansion, because the point of the
    /// quoting is that there is none.
    /// </summary>
    private static class PosixShell
    {
        public static List<string> Split(string line)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            var inWord = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                switch (c)
                {
                    case ' ' or '\t':
                        if (inWord)
                        {
                            words.Add(word.ToString());
                            word.Clear();
                            inWord = false;
                        }

                        continue;

                    case '\'':
                        var close = line.IndexOf('\'', i + 1);
                        Assert.True(close > i, $"Unterminated single quote in: {line}");
                        word.Append(line, i + 1, close - i - 1);
                        i = close;
                        break;

                    case '"':
                        for (i++; line[i] != '"'; i++)
                        {
                            if (line[i] == '\\' && line[i + 1] is '"' or '\\' or '$' or '`')
                            {
                                i++;
                            }

                            word.Append(line[i]);
                        }

                        break;

                    case '\\':
                        word.Append(line[++i]);
                        break;

                    default:
                        word.Append(c);
                        break;
                }

                inWord = true;
            }

            if (inWord)
            {
                words.Add(word.ToString());
            }

            return words;
        }
    }
}
