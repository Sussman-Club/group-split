using Amazon.S3;
using Amazon.S3.Model;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The ways a bank row used to end up somewhere it could not leave, or lose what was
/// attached to it: issue 319.
/// </summary>
/// <remarks>
/// Each was found by reading rather than by somebody hitting it, which is why each is
/// pinned from the person's end -- what they did, and what they can still do afterwards --
/// rather than from the column that was wrong.
/// </remarks>
public class StrandedBankRowTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly FakeBankConnector _bank = new();

    private readonly Mock<IAmazonS3> _storage = new();

    private static readonly DateOnly Day = new(2026, 9, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInboxService Inbox => GetService<IInboxService>();

    private ITransactionService Transactions => GetService<ITransactionService>();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IBankConnector>(FakeBankConnector.Name, _bank);

        _storage.Setup(storage => storage.DeleteObjectAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        services.AddSingleton(_storage.Object);
    }

    // ---- deleting the expense undoes the filing --------------------------------------------

    [Fact]
    public async Task Deleting_an_expense_filed_from_a_row_puts_the_row_back_in_the_inbox()
    {
        var row = await Row(12.50m);
        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        await Transactions.Delete(expense.Id, Ct);

        Assert.Equal(BankTransactionStatus.New, (await Reload(row)).Status);
        Assert.Equal(1, (await Inbox.Summary(ct: Ct)).NewCount);

        // And everything that refused it before works again: it was waiting, not filed.
        var again = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);
        Assert.Equal(row.Id, again.BankTransactionId);
    }

    [Fact]
    public async Task A_row_whose_expense_was_deleted_can_be_ignored()
    {
        var row = await Row(12.50m);
        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);

        await Transactions.Delete(expense.Id, Ct);
        await Inbox.Ignore(row.Id, Ct);

        Assert.Equal(BankTransactionStatus.Ignored, (await Reload(row)).Status);
    }

    /// <summary>
    /// The bank withdrew it after it was filed. Back in the inbox it would be a payment that
    /// no longer exists, so it goes the way a sync deletes a withdrawn row nobody acted on.
    /// </summary>
    [Fact]
    public async Task Deleting_an_expense_whose_row_the_bank_withdrew_takes_the_row_with_it()
    {
        var row = await Row(12.50m);
        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);
        await Withdraw(row);

        await Transactions.Delete(expense.Id, Ct);

        Assert.False(await DbContext.Set<BankTransaction>().AnyAsync(candidate => candidate.Id == row.Id, Ct));
        Assert.Equal(0, (await Inbox.Summary(ct: Ct)).NewCount);
    }

    [Fact]
    public async Task The_receipt_files_a_row_carried_into_its_expense_come_back_with_it()
    {
        var row = await Row(12.50m);
        var fromInbox = await Attach(row.Id, expenseId: null);
        var expense = await Inbox.File(row.Id, new FileBankTransactionRequest(), Ct);
        var addedLater = await Attach(bankTransactionId: null, expenseId: expense.Id);

        await Transactions.Delete(expense.Id, Ct);

        var kept = await Attachment(fromInbox);
        Assert.NotNull(kept);
        Assert.Equal(row.Id, kept.BankTransactionId);
        Assert.Null(kept.ExpenseId);

        // Listed on the row again, where it was before filing.
        Assert.Equal(fromInbox, Assert.Single(
            await GetService<IReceiptAttachmentService>().ListBank(row.Id, Ct)).Id);

        // One uploaded to the expense itself was the expense's, and goes with it -- object
        // and all.
        Assert.Null(await Attachment(addedLater));
        _storage.Verify(storage => storage.DeleteObjectAsync(
            "receipts-test", $"key-{addedLater:N}", It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(storage => storage.DeleteObjectAsync(
            It.IsAny<string>(), $"key-{fromInbox:N}", It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- an expense whose row was withdrawn is not spoken for --------------------------------

    /// <summary>
    /// The case the issue names: a filed pending row is withdrawn, and the charge comes back
    /// as a posted row that does not name it. The expense still points at the withdrawn row,
    /// and that used to take it out of the matcher altogether.
    /// </summary>
    [Fact]
    public async Task Filing_a_row_whose_money_an_expense_already_holds_through_a_withdrawn_row_is_refused()
    {
        var pending = await Row(12.50m, providerId: "pending-1");
        var expense = await Inbox.File(pending.Id, new FileBankTransactionRequest(), Ct);
        await Withdraw(pending);

        var reposted = await Row(12.50m, providerId: "posted-1");

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.File(reposted.Id, new FileBankTransactionRequest(), Ct));

        Assert.Equal(ErrorCodes.PossibleDuplicateExpense, refusal.Code);
        Assert.Equal(expense.Id, Assert.Single(await Inbox.Matches(reposted.Id, Ct)).Expense.Id);
        Assert.Equal(1, await DbContext.Set<Expense>().CountAsync(Ct));
    }

    [Fact]
    public async Task An_expense_whose_row_was_withdrawn_is_offered_the_rows_that_could_be_it()
    {
        var pending = await Row(12.50m, providerId: "pending-1");
        var expense = await Inbox.File(pending.Id, new FileBankTransactionRequest(), Ct);
        await Withdraw(pending);

        var reposted = await Row(12.50m, providerId: "posted-1");

        var stored = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal(reposted.Id, Assert.Single(await GetService<IDuplicateMatcher>().RowsLike(stored, Ct)).Id);
    }

    /// <summary>
    /// "That is the same payment" has to be an answer linking can act on, or the matcher is
    /// offering a choice that fails.
    /// </summary>
    [Fact]
    public async Task Linking_the_reposted_row_moves_the_expense_onto_it_and_retires_the_withdrawn_one()
    {
        var pending = await Row(12.50m, providerId: "pending-1");
        var receipt = await Attach(pending.Id, expenseId: null);
        var expense = await Inbox.File(pending.Id, new FileBankTransactionRequest(), Ct);
        await Withdraw(pending);

        var reposted = await Row(12.50m, providerId: "posted-1");

        await Inbox.Link(reposted.Id, new LinkBankTransactionRequest { TransactionId = expense.Id }, Ct);

        var now = await Reload(reposted);
        var before = await Reload(pending);
        var linked = await DbContext.Set<Expense>().AsNoTracking().SingleAsync(e => e.Id == expense.Id, Ct);

        Assert.Equal(BankTransactionStatus.Filed, now.Status);
        Assert.Equal(pending.Id, now.ReplacesId);
        Assert.Equal(BankTransactionStatus.Superseded, before.Status);
        Assert.Equal(reposted.Id, linked.BankTransactionId);

        // The file the expense carried names the row the expense now points at.
        Assert.Equal(reposted.Id, (await Attachment(receipt))!.BankTransactionId);
    }

    [Fact]
    public async Task An_expense_whose_row_still_stands_cannot_be_linked_to_a_second_one()
    {
        var first = await Row(12.50m, providerId: "t1");
        var expense = await Inbox.File(first.Id, new FileBankTransactionRequest(), Ct);
        var second = await Row(12.50m, providerId: "t2");

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Inbox.Link(second.Id, new LinkBankTransactionRequest { TransactionId = expense.Id }, Ct));

        Assert.Equal(ErrorCodes.TransactionAlreadyImported, refusal.Code);
    }

    // ---- unlinking ---------------------------------------------------------------------------

    /// <summary>
    /// A receipt somebody photographed for a row still in the inbox is a picture of their
    /// spending. When the row goes, the picture goes -- the stored object as well as the row
    /// that named it.
    /// </summary>
    [Fact]
    public async Task Unlinking_deletes_the_receipt_files_of_rows_still_in_the_inbox_and_keeps_the_expenses()
    {
        var waiting = await Row(12.50m, providerId: "t1");
        var filed = await Row(30m, providerId: "t2");
        var waitingFile = await Attach(waiting.Id, expenseId: null);
        var filedFile = await Attach(filed.Id, expenseId: null);
        var expense = await Inbox.File(filed.Id, new FileBankTransactionRequest(), Ct);

        await GetService<IBankConnectionService>().Unlink(await ConnectionId(), Ct);

        Assert.Null(await Attachment(waitingFile));
        _storage.Verify(storage => storage.DeleteObjectAsync(
            "receipts-test", $"key-{waitingFile:N}", It.IsAny<CancellationToken>()), Times.Once);

        var kept = await Attachment(filedFile);
        Assert.NotNull(kept);
        Assert.Equal(expense.Id, kept.ExpenseId);
        Assert.Null(kept.BankTransactionId);
        _storage.Verify(storage => storage.DeleteObjectAsync(
            It.IsAny<string>(), $"key-{filedFile:N}", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unlinking_an_item_the_provider_no_longer_knows_succeeds()
    {
        await Row(12.50m);
        _bank.ItemUnknown = true;

        await GetService<IBankConnectionService>().Unlink(await ConnectionId(), Ct);

        Assert.False(await DbContext.Set<BankConnection>().AnyAsync(Ct));
        Assert.False(await DbContext.Set<BankTransaction>().AnyAsync(Ct));
    }

    /// <summary>
    /// The half-finished unlink: the provider was told, and the save after it failed. Every
    /// retry then met an item the provider had already let go of, and answered 502 for ever.
    /// </summary>
    [Fact]
    public async Task Unlinking_again_after_the_provider_was_already_told_succeeds()
    {
        await Row(12.50m);

        // As far as the first attempt got: the item handed back, nothing deleted here.
        await _bank.RemoveAsync(FakeBankConnector.AccessToken, Ct);

        await GetService<IBankConnectionService>().Unlink(await ConnectionId(), Ct);

        Assert.False(await DbContext.Set<BankConnection>().AnyAsync(Ct));
        Assert.Equal(2, _bank.RemovedTokens.Count);
    }

    [Fact]
    public async Task A_provider_that_will_not_remove_a_known_item_still_stops_the_unlink()
    {
        await Row(12.50m);
        _bank.RefuseRemoval = true;

        var connectionId = await ConnectionId();

        var refusal = await Assert.ThrowsAsync<BadGatewayException>(() =>
            GetService<IBankConnectionService>().Unlink(connectionId, Ct));

        Assert.Equal(ErrorCodes.BankProviderUnavailable, refusal.Code);
        Assert.True(await DbContext.Set<BankConnection>().AnyAsync(Ct));
    }

    // ---- setup ---------------------------------------------------------------------------

    private async Task<Guid> ConnectionId() =>
        (await DbContext.Set<BankConnection>().AsNoTracking().SingleAsync(Ct)).Id;

    private async Task Withdraw(BankTransaction row)
    {
        var tracked = await DbContext.Set<BankTransaction>().SingleAsync(candidate => candidate.Id == row.Id, Ct);
        tracked.RemovedAt = DateTimeOffset.UtcNow;
        await DbContext.SaveChangesAsync(Ct);
    }

    /// <summary>A stored receipt file, as an upload leaves it, with a key the test can name.</summary>
    private async Task<Guid> Attach(Guid? bankTransactionId, Guid? expenseId)
    {
        var id = Guid.NewGuid();

        DbContext.Add(new ReceiptAttachment
        {
            Id = id,
            BankTransactionId = bankTransactionId,
            ExpenseId = expenseId,
            ObjectKey = $"key-{id:N}",
            FileName = "receipt.jpg",
            ContentType = "image/jpeg",
            Length = 10,
            UploadedByUserId = GetService<ICurrentUser>().User.Id,
            UploadedAt = DateTimeOffset.UtcNow
        });

        await DbContext.SaveChangesAsync(Ct);

        return id;
    }

    private Task<ReceiptAttachment?> Attachment(Guid id) =>
        DbContext.Set<ReceiptAttachment>().AsNoTracking().FirstOrDefaultAsync(attachment => attachment.Id == id, Ct);

    private Task<BankTransaction> Reload(BankTransaction row) =>
        DbContext.Set<BankTransaction>().AsNoTracking().SingleAsync(candidate => candidate.Id == row.Id, Ct);

    /// <summary>One imported row on the caller's own connection, waiting in the inbox.</summary>
    private async Task<BankTransaction> Row(decimal amount, string providerId = "t1")
    {
        var connection = await DbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .FirstOrDefaultAsync(candidate => candidate.UserId == GetService<ICurrentUser>().User.Id, Ct);

        if (connection is null)
        {
            connection = new BankConnection
            {
                User = GetService<ICurrentUser>().User,
                Provider = FakeBankConnector.Name,
                ProviderItemId = $"item-{Guid.NewGuid():N}",
                InstitutionName = "Fake Bank",
                AccessTokenCiphertext = GetService<IAccessTokenProtector>().Protect(FakeBankConnector.AccessToken),
                LinkedAt = DateTimeOffset.UtcNow
            };

            connection.Accounts.Add(new LinkedAccount
            {
                ProviderAccountId = "acc-1",
                Name = "Everyday",
                Type = "depository"
            });

            DbContext.Add(connection);
            await DbContext.SaveChangesAsync(Ct);
        }

        var row = new BankTransaction
        {
            Account = connection.Accounts.First(),
            ProviderTransactionId = providerId,
            Date = Day,
            Amount = amount,
            Currency = "USD",
            Description = "LIDL 1234",
            MerchantName = "Lidl",
            RawJson = "{}",
            ImportedAt = DateTimeOffset.UtcNow
        };

        DbContext.Add(row);
        await DbContext.SaveChangesAsync(Ct);

        return row;
    }
}
