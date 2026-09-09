using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Merchants;

/// <summary>
/// Adding and correcting a place by hand. The sync writes this table on its own; this is
/// the other way in, for cash at the same shop every week or a group with no bank linked.
/// </summary>
/// <remarks>
/// What is worth pinning is the shared-ness. One row per place is the whole point of the
/// table, so the folding that the sync's resolver uses has to be the same folding this uses
/// -- otherwise somebody types "lidl", a sync later reports "Lidl", and the app has two of
/// them with the logo on one.
/// </remarks>
public class MerchantServiceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMerchantService Merchants => GetService<IMerchantService>();

    [Fact]
    public async Task A_place_added_by_hand_is_folded_the_way_a_synced_one_is()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest
        {
            Name = "  Blue Bottle Coffee ",
            LogoUrl = "https://logos/bb.png"
        }, Ct);

        Assert.Equal("Blue Bottle Coffee", merchant.Name);
        Assert.Equal("blue bottle coffee", merchant.NormalizedName);
        Assert.Equal("https://logos/bb.png", merchant.LogoUrl);
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
    public async Task The_same_place_cannot_be_added_twice_under_a_different_casing()
    {
        await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Merchants.Create(new CreateMerchantRequest { Name = "LIDL" }, Ct));

        Assert.Equal(ErrorCodes.MerchantNameTaken, refusal.Code);
        Assert.Single(await DbContext.Set<Merchant>().ToListAsync(Ct));
    }

    [Fact]
    public async Task Renaming_a_place_re_folds_its_key_so_the_next_sync_still_finds_it()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "LIDL GmbH" }, Ct);

        var renamed = await Merchants.Update(merchant.Id, new UpdateMerchantRequest { Name = "Lidl" }, Ct);

        Assert.Equal("Lidl", renamed.Name);

        // The column the unique index is on, and the one the resolver looks up. Left stale
        // it would be a row nothing could ever find again -- and a second Lidl next sync.
        Assert.Equal("lidl", renamed.NormalizedName);
    }

    [Fact]
    public async Task An_update_that_omits_the_logo_clears_it()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest
        {
            Name = "Lidl",
            LogoUrl = "https://logos/lidl.png"
        }, Ct);

        await Merchants.Update(merchant.Id, new UpdateMerchantRequest { Name = "Lidl" }, Ct);

        // Deliberately unlike the sync, which only ever fills a missing logo in: a provider
        // that stops sending one is having a bad afternoon, and a person who clears the
        // field means it.
        Assert.Null((await Merchants.Get(merchant.Id, Ct)).LogoUrl);
    }

    [Fact]
    public async Task Renaming_onto_a_name_another_place_already_has_is_refused()
    {
        await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        var other = await Merchants.Create(new CreateMerchantRequest { Name = "Aldi" }, Ct);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Merchants.Update(other.Id, new UpdateMerchantRequest { Name = "lidl" }, Ct));

        Assert.Equal(ErrorCodes.MerchantNameTaken, refusal.Code);
    }

    [Fact]
    public async Task A_place_keeps_its_own_name_through_an_update_that_does_not_change_it()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);

        // The excluding clause: without it a row would collide with itself and no merchant
        // could ever be given a logo.
        var updated = await Merchants.Update(merchant.Id, new UpdateMerchantRequest
        {
            Name = "Lidl",
            LogoUrl = "https://logos/lidl.png"
        }, Ct);

        Assert.Equal("https://logos/lidl.png", updated.LogoUrl);
    }

    [Fact]
    public async Task A_place_nothing_points_at_can_be_deleted()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);

        await Merchants.Delete(merchant.Id, Ct);

        Assert.Empty(await DbContext.Set<Merchant>().ToListAsync(Ct));
    }

    [Fact]
    public async Task A_place_an_expense_points_at_cannot_be_deleted()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        await ExpenseAt(merchant);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Merchants.Delete(merchant.Id, Ct));

        Assert.Equal(ErrorCodes.MerchantInUse, refusal.Code);
        Assert.Single(await DbContext.Set<Merchant>().ToListAsync(Ct));
    }

    [Fact]
    public async Task A_place_only_a_waiting_bank_row_points_at_cannot_be_deleted_either()
    {
        var merchant = await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        await ImportedRowAt(merchant);

        // A row still waiting in an inbox points here too, and deleting under it would
        // leave the inbox unable to render what it is offering.
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Merchants.Delete(merchant.Id, Ct));

        Assert.Equal(ErrorCodes.MerchantInUse, refusal.Code);
    }

    [Fact]
    public async Task A_place_that_is_not_there_is_a_not_found_rather_than_a_null()
    {
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Merchants.Get(Guid.NewGuid(), Ct));

        Assert.Equal(ErrorCodes.MerchantNotFound, missing.Code);
    }

    [Fact]
    public async Task The_listing_narrows_on_the_folded_name()
    {
        await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        await Merchants.Create(new CreateMerchantRequest { Name = "Aldi" }, Ct);

        // Upper-cased on the way in, to prove the search folds rather than matching what
        // the caller happened to type.
        var found = await (await Merchants.List("LID", Ct)).ToListAsync(Ct);

        Assert.Equal("Lidl", Assert.Single(found).Name);
    }

    [Fact]
    public async Task A_listing_with_no_search_is_everything()
    {
        await Merchants.Create(new CreateMerchantRequest { Name = "Lidl" }, Ct);
        await Merchants.Create(new CreateMerchantRequest { Name = "Aldi" }, Ct);

        Assert.Equal(2, (await (await Merchants.List(null, Ct)).ToListAsync(Ct)).Count);
    }

    /// <summary>
    /// A place is not a group's, so every caller sees every one of them.
    /// </summary>
    /// <remarks>
    /// The one unscoped read in the app, and worth a test saying so on purpose rather than
    /// leaving it to look like an oversight. Nothing leaks: a name and a logo are what a
    /// bank would have told either group anyway, and the row says nothing about who spent
    /// what.
    /// </remarks>
    [Fact]
    public async Task A_place_somebody_else_added_is_visible_here_too()
    {
        var merchant = new Merchant
        {
            Name = "Lidl",
            NormalizedName = "lidl",
            FirstSeenAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(merchant);
        await DbContext.SaveChangesAsync(Ct);

        Assert.Equal(merchant.Id, (await Merchants.Get(merchant.Id, Ct)).Id);
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

    private async Task ExpenseAt(Merchant merchant)
    {
        var user = GetService<ICurrentUser>().User;

        var expense = new Expense
        {
            User = user,
            Amount = 12.50m,
            Currency = "USD",
            Name = "Weekly shop",
            DateTime = DateTimeOffset.UtcNow,
            MerchantId = merchant.Id
        };

        expense.Splits.Add(new TransactionSplit { User = user, Amount = 12.50m });

        DbContext.Add(expense);
        await DbContext.SaveChangesAsync(Ct);
    }

    private async Task ImportedRowAt(Merchant merchant)
    {
        var user = GetService<ICurrentUser>().User;

        var connection = new BankConnection
        {
            User = user,
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
            ProviderTransactionId = "t1",
            Date = new DateOnly(2026, 9, 1),
            Amount = 12.50m,
            Description = "LIDL 1234",
            MerchantName = "Lidl",
            MerchantId = merchant.Id,
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        });

        connection.Accounts.Add(account);

        DbContext.Add(connection);
        await DbContext.SaveChangesAsync(Ct);
    }
}
