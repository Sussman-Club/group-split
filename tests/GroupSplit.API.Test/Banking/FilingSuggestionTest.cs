using System.Text.Json;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The classifier that suggests a group and category for an imported row (#437). The model
/// is faked: what is pinned here is what it is asked, and what of its answer survives --
/// never a destination the person cannot file into, never a guess it is unsure of, and
/// never a suggestion on a row that could already be recorded.
/// </summary>
public class FilingSuggestionTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();
    private readonly FakeDecisionClient _model = new();

    private bool _configured = true;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);
        services.AddSingleton<IDecisionClient>(_model);
        services.Configure<FilingSuggestionOptions>(options =>
        {
            if (!_configured)
                return;

            options.Deployment = "Decision-1";
            options.Endpoint = new Uri("https://decision.test/");
            options.Key = "test-key";
        });
    }

    [Fact]
    public async Task Receipt_suggestions_are_limited_to_the_selected_group_and_do_not_import_a_row()
    {
        var (group, groceries) = await Home();
        _model.Answer("Home · Groceries", 0.9);
        var before = await GetService<GroupSplit.Data.AppDbContext>().Set<BankTransaction>().CountAsync(Ct);
        var suggestion = await GetService<IFilingSuggester>().SuggestReceipt(new SuggestReceiptFilingRequest
        {
            GroupId = group.Id, Currency = "USD",
            Receipt = new SaveReceiptRequest
            {
                MerchantName = "Corner shop", Total = 12m,
                Items = [new ReceiptItemInput { Name = "Milk" }]
            }
        }, Ct);
        Assert.NotNull(suggestion);
        Assert.Equal(groceries, suggestion.CategoryId);
        Assert.DoesNotContain(Assert.Single(_model.Asked).Options.Keys, label => label.StartsWith("Personal"));
        Assert.Equal(before, await GetService<GroupSplit.Data.AppDbContext>().Set<BankTransaction>().CountAsync(Ct));
    }

    [Fact]
    public async Task Receipt_suggestions_cache_the_spending_facts_and_refresh_when_the_amount_changes()
    {
        var (group, _) = await Home();
        _model.Answer("Home · Groceries", .9);
        var request = new SuggestReceiptFilingRequest
        {
            GroupId = group.Id, Currency = "USD",
            Receipt = new SaveReceiptRequest { MerchantName = "Corner shop", Total = 12m,
                Items = [new ReceiptItemInput { Name = "Milk" }] }
        };
        var service = GetService<IFilingSuggester>();
        Assert.NotNull(await service.SuggestReceipt(request, Ct));
        Assert.NotNull(await service.SuggestReceipt(request, Ct));
        Assert.Single(_model.Asked);
        Assert.NotNull(await service.SuggestReceipt(request with { Receipt = request.Receipt with { Total = 24m } }, Ct));
        Assert.Equal(2, _model.Asked.Count);
    }

    [Fact]
    public async Task Suggests_the_group_and_category_the_model_picks()
    {
        var (group, groceries) = await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        var suggestion = Assert.Single(await Suggest(row));

        Assert.Equal(row.Id, suggestion.BankTransactionId);
        Assert.Equal(group.Id, suggestion.GroupId);
        Assert.Equal(groceries, suggestion.CategoryId);
        Assert.False(suggestion.IsPersonal);
        Assert.Equal("Home · Groceries", suggestion.Destination);
        Assert.Equal(0.9, suggestion.Confidence);
    }

    [Fact]
    public async Task Every_option_is_somewhere_the_person_can_file_and_cannot_tell_is_one_of_them()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        await Suggest(row);

        var asked = Assert.Single(_model.Asked);
        Assert.Contains("Home · Groceries", asked.Options.Keys);
        Assert.Contains(FilingSuggester.CannotTell, asked.Options.Keys);
    }

    [Fact]
    public async Task An_answer_that_was_not_offered_is_dropped()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer("Somebody Else's · Holiday", 0.99);

        Assert.Empty(await Suggest(row));
    }

    [Fact]
    public async Task Cannot_tell_is_no_suggestion()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer(FilingSuggester.CannotTell, 0.95);

        Assert.Empty(await Suggest(row));
    }

    [Fact]
    public async Task An_answer_below_the_threshold_is_treated_as_abstaining()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.4);

        Assert.Empty(await Suggest(row));
    }

    [Fact]
    public async Task A_row_that_could_already_be_recorded_gets_none_and_the_model_is_not_asked()
    {
        await Home();

        // Typed in by hand: an expense filed from another bank row is that row's, and is
        // never offered as a duplicate of this one.
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Lidl",
            Amount = 30m,
            DateTime = new DateTime(2026, 9, 1, 20, 30, 0, DateTimeKind.Utc)
        }, Ct);

        var again = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        Assert.Empty(await Suggest(again));
        Assert.Empty(_model.Asked);
    }

    [Fact]
    public async Task Money_coming_in_is_not_asked_about()
    {
        await Home();
        var refund = await Row(-12m);
        _model.Answer("Home · Groceries", 0.9);

        Assert.Empty(await Suggest(refund));
        Assert.Empty(_model.Asked);
    }

    [Fact]
    public async Task A_group_in_another_currency_is_not_offered()
    {
        await Home();
        var row = await Row(30m, currency: "EUR");
        _model.Answer("Home · Groceries", 0.9);

        await Suggest(row);

        // Asked only if the personal group has categories; either way, Home is not offered.
        Assert.DoesNotContain(_model.Asked, asked => asked.Options.ContainsKey("Home · Groceries"));
    }

    [Fact]
    public async Task What_earlier_charges_from_the_merchant_became_is_given_as_evidence()
    {
        var (group, groceries) = await Home();
        var lidl = await Merchant("Lidl");

        var earlier = await Row(18m, providerId: "earlier", date: new DateOnly(2026, 8, 1), merchant: lidl);
        await Inbox.File(earlier.Id, new FileBankTransactionRequest { GroupId = group.Id, CategoryId = groceries }, Ct);

        var row = await Row(30m, merchant: lidl);
        _model.Answer("Home · Groceries", 0.9);

        await Suggest(row);

        var asked = Assert.Single(_model.Asked);
        Assert.Contains("Home · Groceries: 1 of the last 1", asked.State);
    }

    [Fact]
    public async Task The_receipt_read_for_the_row_is_given_as_evidence()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        await Inbox.Suggest(new SuggestFilingsRequest
        {
            Rows =
            [
                new SuggestFilingRow
                {
                    Id = row.Id,
                    Receipt = new FilingReceipt { MerchantName = "LIDL", Items = ["Nappies", "Whole milk"] }
                }
            ]
        }, Ct);

        var asked = Assert.Single(_model.Asked);
        Assert.Contains("Nappies", asked.State);
        Assert.Contains("Whole milk", asked.State);
    }

    [Fact]
    public async Task A_description_is_sent_with_the_option_when_one_is_set()
    {
        var (group, groceries) = await Home();
        var category = await DbContext.Set<Category>().SingleAsync(candidate => candidate.Id == groceries, Ct);
        group.Description = "The flat we share near the station";
        category.Description = "Food and household supplies";
        await DbContext.SaveChangesAsync(Ct);
        var row = await Row(30m);
        _model.Answer("Home � Groceries", 0.9);

        await Suggest(row);

        var options = Assert.Single(_model.Asked).Options.Values;
        Assert.Contains(options, option => option.Contains("The flat we share near the station"));
        Assert.Contains(options, option => option.Contains("Food and household supplies"));
    }

    [Fact]
    public async Task The_same_inputs_are_answered_from_the_cache()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer("Home � Groceries", 0.9);

        await Suggest(row);
        await Suggest(row);

        Assert.Single(_model.Asked);
    }

    [Fact]
    public async Task Changing_a_destination_description_gets_a_fresh_answer()
    {
        var (_, groceries) = await Home();
        var row = await Row(30m);
        _model.Answer("Home � Groceries", 0.9);

        await Suggest(row);
        var category = await DbContext.Set<Category>().SingleAsync(candidate => candidate.Id == groceries, Ct);
        category.Description = "Food for everybody in the flat";
        await DbContext.SaveChangesAsync(Ct);
        await Suggest(row);

        Assert.Equal(2, _model.Asked.Count);
    }

    [Fact]
    public async Task Member_names_never_go_to_the_model()
    {
        var (group, _) = await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        await Suggest(row);

        var asked = Assert.Single(_model.Asked);
        var everything = asked.State + string.Join(" ", asked.Options.Values);

        var members = await DbContext.Set<Data.Entities.User>()
            .Where(user => user.Groups.Any(g => g.Id == group.Id))
            .ToListAsync(Ct);

        Assert.NotEmpty(members);

        foreach (var name in members.SelectMany(user => new[] { user.FirstName, user.LastName, user.Email }).OfType<string>())
            Assert.DoesNotContain(name, everything);
    }

    [Fact]
    public async Task A_failed_call_is_no_suggestion_rather_than_an_error()
    {
        await Home();
        var row = await Row(30m);
        _model.Fail();

        Assert.Empty(await Suggest(row));
    }

    [Fact]
    public async Task A_failed_call_is_not_cached()
    {
        await Home();
        var row = await Row(30m);
        _model.Fail();

        Assert.Empty(await Suggest(row));

        _model.AnswerWithFirstOption(0.9);
        Assert.Single(await Suggest(row));
        Assert.Equal(2, _model.Asked.Count);
    }

    [Fact]
    public async Task With_no_model_configured_nothing_is_asked()
    {
        _configured = false;
        await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        Assert.Empty(await Suggest(row));
        Assert.Empty(_model.Asked);
    }

    [Fact]
    public async Task Somebody_elses_row_gets_nothing()
    {
        await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        await CreateNewUser();
        using var scope = ServiceProvider.CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);
        var stranger = scope.ServiceProvider.GetRequiredService<IInboxService>();

        Assert.Empty(await stranger.Suggest(new SuggestFilingsRequest { Rows = [new SuggestFilingRow { Id = row.Id }] }, Ct));
    }

    [Fact]
    public async Task Accepting_a_suggestion_files_exactly_as_picking_it_by_hand_would()
    {
        var (group, groceries) = await Home();
        var row = await Row(30m);
        _model.Answer("Home · Groceries", 0.9);

        var suggestion = Assert.Single(await Suggest(row));
        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest
        {
            GroupId = suggestion.GroupId,
            CategoryId = suggestion.CategoryId
        }, Ct);

        Assert.Equal(group.Id, expense.GroupId);
        Assert.Equal(groceries, expense.CategoryId);
        Assert.Equal(2, await DbContext.Set<TransactionSplit>().CountAsync(split => split.TransactionId == expense.Id, Ct));
    }

    private Task<IReadOnlyList<FilingSuggestionResponse>> Suggest(BankTransaction row) =>
        Inbox.Suggest(new SuggestFilingsRequest { Rows = [new SuggestFilingRow { Id = row.Id }] }, Ct);

    /// <summary>A two-person group with an evenly divided Groceries category.</summary>
    private async Task<(Data.Entities.Group Group, Guid Groceries)> Home()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Home" }, Ct);
        await JoinGroup(group.Id, await CreateNewUser());

        return (group, await CreateEvenCategory(group.Id, "Groceries"));
    }

    private async Task<Merchant> Merchant(string name)
    {
        var merchant = new Merchant { Name = name, NormalizedName = name.ToLowerInvariant(), FirstSeenAt = DateTimeOffset.UtcNow };
        DbContext.Add(merchant);
        await DbContext.SaveChangesAsync(Ct);
        return merchant;
    }

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    private async Task<BankTransaction> Row(decimal amount, string currency = "USD", string providerId = "t1",
        DateOnly? date = null, Merchant? merchant = null)
    {
        var me = GetService<ICurrentUser>().User;

        var connection = await DbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .FirstOrDefaultAsync(candidate => candidate.UserId == me.Id, Ct);

        if (connection is null)
        {
            connection = new BankConnection
            {
                User = me,
                Provider = FakeBankConnector.Name,
                ProviderItemId = $"item-{Guid.NewGuid():N}",
                InstitutionName = "Fake Bank",
                AccessTokenCiphertext = GetService<IAccessTokenProtector>().Protect(FakeBankConnector.AccessToken),
                LinkedAt = DateTimeOffset.UtcNow
            };

            connection.Accounts.Add(new LinkedAccount { ProviderAccountId = "acc-1", Name = "Everyday", Type = "depository" });

            DbContext.Add(connection);
            await DbContext.SaveChangesAsync(Ct);
        }

        var row = new BankTransaction
        {
            Account = connection.Accounts.First(),
            ProviderTransactionId = providerId,
            Date = date ?? new DateOnly(2026, 9, 1),
            Amount = amount,
            Currency = currency,
            Description = "LIDL 1234",
            MerchantName = merchant?.Name ?? "Lidl",
            Merchant = merchant,
            ProviderCategory = "FOOD_AND_DRINK",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        return row;
    }

    /// <summary>
    /// Answers with whatever it was told to, and keeps what it was asked -- the state as the
    /// JSON the real client would send, so a test can look for a word in it.
    /// </summary>
    private sealed class FakeDecisionClient : IDecisionClient
    {
        /// <summary>Unescaped, so "Home · Groceries" can be looked for as written.</summary>
        private static readonly JsonSerializerOptions Readable = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private DecisionAnswer? _answer;
        private bool _fail;
        private double? _firstOptionConfidence;

        public List<(string State, IReadOnlyDictionary<string, string> Options)> Asked { get; } = [];

        public void Answer(string choice, double confidence)
        {
            _fail = false;
            _firstOptionConfidence = null;
            _answer = new DecisionAnswer(choice, confidence);
        }

        public void AnswerWithFirstOption(double confidence)
        {
            _fail = false;
            _answer = null;
            _firstOptionConfidence = confidence;
        }

        public void Fail() => _fail = true;

        public Task<DecisionAnswer?> ChooseAsync(
            object state, string instructions, IReadOnlyDictionary<string, string> options, CancellationToken ct = default)
        {
            lock (Asked)
                Asked.Add((JsonSerializer.Serialize(state, Readable), options));

            return _fail
                ? Task.FromException<DecisionAnswer?>(new HttpRequestException("model unavailable"))
                : Task.FromResult(_firstOptionConfidence is { } confidence
                    ? new DecisionAnswer(options.Keys.First(choice => choice != FilingSuggester.CannotTell), confidence)
                    : _answer);
        }
    }
}
