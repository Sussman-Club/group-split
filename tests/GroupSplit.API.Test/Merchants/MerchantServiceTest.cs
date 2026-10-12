using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Merchants;

/// <summary>
/// Adding a place by hand, and reading the places that are yours. The sync writes this
/// table on its own; this is the other way in, for cash at the same shop every week or a
/// group with no bank linked.
/// </summary>
/// <remarks>
/// Two things are worth pinning. The shared-ness: one row per place is the whole point of
/// the table, so the folding the sync's resolver uses has to be the same folding this uses.
/// And the limits of it: a row being shared does not make it everybody's to read or to
/// change -- a caller sees the places they already have something at, and nobody renames,
/// relogos or deletes one.
/// </remarks>
public class MerchantServiceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private const string PlaidLogo = "https://plaid-merchant-logos.plaid.com/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMerchantService Merchants => GetService<IMerchantService>();

    private Task<Merchant> Stored(Guid id) =>
        DbContext.Set<Merchant>().AsNoTracking().FirstAsync(merchant => merchant.Id == id, Ct);

    // ---- adding one ----------------------------------------------------------------------

    [Fact]
    public async Task A_place_added_by_hand_is_folded_the_way_a_synced_one_is()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest
        {
            Name = "  Blue Bottle Coffee ",
            LogoUrl = PlaidLogo + "bb.png"
        }, Ct);

        Assert.Equal("Blue Bottle Coffee", merchant.Name);
        Assert.Equal("blue bottle coffee", (await Stored(merchant.Id)).NormalizedName);
        Assert.Equal(PlaidLogo + "bb.png", merchant.LogoUrl);
    }

    [Fact]
    public async Task A_place_with_a_blank_logo_gets_none_rather_than_an_empty_string()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest
        {
            Name = "Corner shop",
            LogoUrl = "   "
        }, Ct);

        // An empty src resolves to the page itself and renders as a broken image, so the
        // whitespace is stored as the null it means.
        Assert.Null(merchant.LogoUrl);
    }

    [Fact]
    public async Task A_place_can_carry_one_of_the_marks_the_app_ships()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest
        {
            Name = "Lidl",
            LogoUrl = MerchantLogos.BundledPath + "lidl.svg"
        }, Ct);

        Assert.Equal("/_content/GroupSplit.App.Shared/merchants/lidl.svg", merchant.LogoUrl);
    }

    /// <summary>
    /// Every viewer of every ledger that shows this place loads its logo, so an address
    /// anybody can type is a tracking pixel on other people's screens.
    /// </summary>
    [Fact]
    public async Task A_logo_on_a_host_that_is_not_the_providers_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Merchants.Create(new CreateMerchantRequest
            {
                Name = "Lidl",
                LogoUrl = "https://tracker.example.com/pixel.png"
            }, Ct));

        Assert.Equal(ErrorCodes.MerchantLogoNotAllowed, refusal.Code);
        Assert.Empty(await DbContext.Set<Merchant>().ToListAsync(Ct));
    }

    /// <summary>
    /// The caller cannot see a place they have nothing at, so "already there" is not
    /// something they could have known. Handing back the row is what the resolver does
    /// with the same name; refusing would leave them unable to name it at all.
    /// </summary>
    [Fact]
    public async Task Adding_a_place_that_is_already_there_hands_back_the_one_there_is()
    {
        var first = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl", LogoUrl = PlaidLogo + "lidl.png" }, Ct);

        var second = await Merchants.Create(new CreateMerchantRequest { Name = "LIDL", LogoUrl = PlaidLogo + "other.png" }, Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await DbContext.Set<Merchant>().ToListAsync(Ct));

        // Not a relogo by the back door.
        Assert.Equal(PlaidLogo + "lidl.png", (await Stored(first.Id)).LogoUrl);
    }

    // ---- changing one ----------------------------------------------------------------------

    [Fact]
    public async Task Another_user_cannot_rename_or_relogo_a_place()
    {
        var merchant = await SomebodyElsesPlace("Lidl", PlaidLogo + "lidl.png");

        var refusal = await Assert.ThrowsAsync<ForbiddenException>(() =>
            Merchants.Update(merchant.Id, new UpdateMerchantRequest
            {
                Name = "Somewhere rude",
                LogoUrl = PlaidLogo + "other.png"
            }, Ct));

        Assert.Equal(ErrorCodes.MerchantEditForbidden, refusal.Code);

        var stored = await Stored(merchant.Id);
        Assert.Equal("Lidl", stored.Name);
        Assert.Equal("lidl", stored.NormalizedName);
        Assert.Equal(PlaidLogo + "lidl.png", stored.LogoUrl);
    }

    [Fact]
    public async Task Another_user_cannot_delete_a_place()
    {
        var merchant = await SomebodyElsesPlace("Lidl");

        var refusal = await Assert.ThrowsAsync<ForbiddenException>(() => Merchants.Delete(merchant.Id, Ct));

        Assert.Equal(ErrorCodes.MerchantEditForbidden, refusal.Code);
        Assert.Single(await DbContext.Set<Merchant>().ToListAsync(Ct));
    }

    /// <summary>
    /// The same answer for a merchant that does not exist, so the refusal cannot be used to
    /// find out which ids are real.
    /// </summary>
    [Fact]
    public async Task Changing_a_place_that_is_not_there_is_refused_the_same_way()
    {
        var refusal = await Assert.ThrowsAsync<ForbiddenException>(() =>
            Merchants.Update(Guid.NewGuid(), new UpdateMerchantRequest { Name = "Lidl" }, Ct));

        Assert.Equal(ErrorCodes.MerchantEditForbidden, refusal.Code);
    }

    // ---- reading them ----------------------------------------------------------------------

    [Fact]
    public async Task A_place_that_is_not_there_is_a_not_found_rather_than_a_null()
    {
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Merchants.Get(Guid.NewGuid(), Ct));

        Assert.Equal(ErrorCodes.MerchantNotFound, missing.Code);
    }

    /// <summary>
    /// The privacy half of the issue: a place only somebody else's card has paid -- a
    /// clinic, a lawyer -- says something about them, and is not in anybody else's list.
    /// </summary>
    [Fact]
    public async Task A_place_only_another_user_has_been_is_not_listed()
    {
        await SomebodyElsesPlace("Dr. Ruiz Psychiatry");

        Assert.Empty(await (await Merchants.List(null, Ct)).ToListAsync(Ct));
        Assert.Empty(await (await Merchants.List("ruiz", Ct)).ToListAsync(Ct));
    }

    [Fact]
    public async Task A_place_only_another_user_has_been_cannot_be_read_by_id()
    {
        var merchant = await SomebodyElsesPlace("Dr. Ruiz Psychiatry");

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Merchants.Get(merchant.Id, Ct));

        Assert.Equal(ErrorCodes.MerchantNotFound, missing.Code);
    }

    [Fact]
    public async Task A_place_somebody_in_my_group_spent_at_is_mine_to_see()
    {
        var me = GetService<ICurrentUser>().User;
        var friend = await CreateNewUser();
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        await JoinGroup(group.Id, friend);

        var merchant = await Place("Lidl");
        await ExpenseAt(merchant, friend, group.Id);

        // Their expense is on my ledger already, so its place is not news to me.
        var listed = Assert.Single(await (await Merchants.List("lid", Ct)).ToListAsync(Ct));
        Assert.Equal(merchant.Id, listed.Id);
        Assert.NotEqual(me.Id, friend.Id);
    }

    /// <summary>
    /// Somewhere I paid stays mine after I leave the group I paid it in. Only one of the
    /// three ways a place becomes mine says so -- the group's expenses are no longer -- so
    /// this is the test that notices if that branch goes missing.
    /// </summary>
    [Fact]
    public async Task A_place_i_paid_in_a_group_i_have_left_is_still_mine_to_see()
    {
        var me = GetService<ICurrentUser>().User;
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Old flat" }, Ct);

        var merchant = await Place("Lidl");
        await ExpenseAt(merchant, me, group.Id);

        var membership = await DbContext.Set<GroupMembership>()
            .FirstAsync(row => row.GroupId == group.Id && row.UserId == me.Id, Ct);
        membership.LeftAt = DateTimeOffset.UtcNow;
        membership.LeftReason = LeftReason.Left;
        await DbContext.SaveChangesAsync(Ct);

        var listed = Assert.Single(await (await Merchants.List("lid", Ct)).ToListAsync(Ct));
        Assert.Equal(merchant.Id, listed.Id);
    }

    [Fact]
    public async Task A_place_my_own_bank_imported_is_mine_to_see()
    {
        var merchant = await Place("Lidl");
        await ImportedRowAt(merchant, GetService<ICurrentUser>().User);

        Assert.Equal(merchant.Id, (await Merchants.Get(merchant.Id, Ct)).Id);
    }

    [Fact]
    public async Task A_place_another_users_bank_imported_is_not()
    {
        var merchant = await Place("Dr. Ruiz Psychiatry");
        await ImportedRowAt(merchant, await CreateNewUser());

        Assert.Empty(await (await Merchants.List(null, Ct)).ToListAsync(Ct));
    }

    /// <summary>
    /// How much other people spend at a place is no more the caller's to learn than which
    /// places those are.
    /// </summary>
    [Fact]
    public async Task The_count_is_of_my_own_transactions_there_not_everybodys()
    {
        var merchant = await Place("Lidl");
        await ExpenseAt(merchant, GetService<ICurrentUser>().User);
        var stranger = await CreateNewUser();
        await ExpenseAt(merchant, stranger);
        await ExpenseAt(merchant, stranger);

        Assert.Equal(1, (await Merchants.Get(merchant.Id, Ct)).TransactionCount);
    }

    [Fact]
    public async Task The_listing_narrows_on_the_folded_name()
    {
        await Mine("Lidl");
        await Mine("Aldi");

        // Upper-cased on the way in, to prove the search folds rather than matching what
        // the caller happened to type.
        var found = await (await Merchants.List("LID", Ct)).ToListAsync(Ct);

        Assert.Equal("Lidl", Assert.Single(found).Name);
    }

    [Fact]
    public async Task A_listing_with_no_search_is_every_place_of_mine()
    {
        await Mine("Lidl");
        await Mine("Aldi");
        await SomebodyElsesPlace("Costco");

        var names = (await (await Merchants.List(null, Ct)).ToListAsync(Ct)).Select(merchant => merchant.Name);

        Assert.Equal(["Aldi", "Lidl"], names);
    }

    [Fact]
    public async Task A_listing_stops_at_twenty()
    {
        for (var i = 0; i < 25; i++)
            await Mine($"Shop {i:00}");

        var listed = await (await Merchants.List("shop", Ct)).ToListAsync(Ct);

        Assert.Equal(MerchantService.PageSize, listed.Count);
        Assert.Equal(20, listed.Count);
        Assert.Equal("Shop 00", listed[0].Name);
    }

    [Theory]
    [InlineData("l")]
    [InlineData(" l ")]
    public async Task A_one_letter_search_is_refused(string search)
    {
        var refusal = await Assert.ThrowsAsync<ValidationException>(() => Merchants.List(search, Ct));

        Assert.Equal(ErrorCodes.MerchantSearchTooShort, refusal.Code);
    }

    // ---- naming a shop when recording an expense -------------------------------------

    [Fact]
    public async Task An_expense_can_be_recorded_at_a_place()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);

        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Weekly shop",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            MerchantId = merchant.Id
        }, Ct);

        Assert.Equal(merchant.Id, expense.MerchantId);

        // And that is what makes it the caller's: a place they have just named is in their
        // listing from the expense on.
        Assert.Equal(merchant.Id, (await Merchants.Get(merchant.Id, Ct)).Id);
    }

    [Fact]
    public async Task An_expense_cannot_be_recorded_at_a_place_that_is_not_there()
    {
        // AsTask because Create hands back a ValueTask, which ThrowsAsync cannot await.
        var missing = await Assert.ThrowsAsync<NotFoundException>(() =>
            GetService<ITransactionService>().Create(new CreateTransactionRequest
            {
                Name = "Weekly shop",
                Amount = 40m,
                DateTime = DateTimeOffset.UtcNow,
                MerchantId = Guid.NewGuid()
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.MerchantNotFound, missing.Code);
    }

    /// <summary>
    /// An edit that says nothing about the shop leaves the expense where it was spent.
    /// </summary>
    /// <remarks>
    /// The model a patch is applied to has to carry it, or an ordinary read-change-write --
    /// "correct the amount" -- would quietly forget the shop. That is the same trap the
    /// splits are deliberately on the other side of, and the reason they are commented
    /// where they are.
    /// </remarks>
    [Fact]
    public async Task Editing_the_amount_does_not_forget_where_it_was_spent()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        var transactions = GetService<ITransactionService>();

        var expense = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Weekly shop",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            MerchantId = merchant.Id
        }, Ct);

        var model = await transactions.GetUpdateModel(expense.Id, Ct);
        Assert.NotNull(model);
        Assert.Equal(merchant.Id, model.MerchantId);

        model.Amount = 45m;
        var updated = await transactions.Update(expense.Id, model, Ct);

        Assert.Equal(45m, updated.Amount);
        Assert.Equal(merchant.Id, updated.MerchantId);
    }

    [Fact]
    public async Task An_expense_can_be_told_to_forget_where_it_was_spent()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        var transactions = GetService<ITransactionService>();

        var expense = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Weekly shop",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            MerchantId = merchant.Id
        }, Ct);

        var model = await transactions.GetUpdateModel(expense.Id, Ct);
        Assert.NotNull(model);
        model.MerchantId = null;

        Assert.Null((await transactions.Update(expense.Id, model, Ct)).MerchantId);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>A merchant row nobody points at yet, written straight to the table.</summary>
    private async Task<Merchant> Place(string name, string? logo = null)
    {
        var merchant = new Merchant
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            LogoUrl = logo,
            FirstSeenAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(merchant);
        await DbContext.SaveChangesAsync(Ct);

        return merchant;
    }

    /// <summary>A place the caller has a personal expense at.</summary>
    private async Task<Merchant> Mine(string name)
    {
        var merchant = await Place(name);
        await ExpenseAt(merchant, GetService<ICurrentUser>().User);
        return merchant;
    }

    /// <summary>A place only a stranger -- somebody sharing no group with the caller -- has paid.</summary>
    private async Task<Merchant> SomebodyElsesPlace(string name, string? logo = null)
    {
        var merchant = await Place(name, logo);
        await ExpenseAt(merchant, await CreateNewUser());
        return merchant;
    }

    private async Task ExpenseAt(Merchant merchant, Data.Entities.User payer, Guid? groupId = null)
    {
        var expense = new Expense
        {
            UserId = payer.Id,
            GroupId = groupId ?? payer.Id,
            Amount = 12.50m,
            Currency = "USD",
            Name = "Weekly shop",
            DateTime = DateTimeOffset.UtcNow,
            MerchantId = merchant.Id
        };

        expense.Splits.Add(new TransactionSplit { UserId = payer.Id, Amount = 12.50m });

        DbContext.Add(expense);
        await DbContext.SaveChangesAsync(Ct);
    }

    private async Task ImportedRowAt(Merchant merchant, Data.Entities.User owner)
    {
        var connection = new BankConnection
        {
            UserId = owner.Id,
            Provider = "seed",
            ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Probe Bank",
            AccessTokenCiphertext = "x",
            LinkedAt = DateTimeOffset.UtcNow
        };

        var account = new LinkedAccount
        {
            ProviderAccountId = "acc-1",
            Name = "Everyday",
            Type = "depository"
        };

        account.Transactions.Add(new BankTransaction
        {
            ProviderTransactionId = $"t-{Guid.NewGuid():N}",
            Date = new DateOnly(2026, 9, 1),
            Amount = 12.50m,
            Description = "LIDL 1234",
            MerchantName = merchant.Name,
            MerchantId = merchant.Id,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        });

        connection.Accounts.Add(account);

        DbContext.Add(connection);
        await DbContext.SaveChangesAsync(Ct);
    }
}
