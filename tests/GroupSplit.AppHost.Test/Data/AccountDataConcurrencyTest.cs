using Amazon.S3;
using Amazon.S3.Model;
using Aspire.Hosting.Testing;
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking;
using GroupSplit.AppHost.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.Json;

namespace GroupSplit.AppHost.Test.Data;

/// <summary>Real PostgreSQL sessions, independent scopes and deliberately paused external calls.</summary>
public class AccountDataConcurrencyTest(AppHostFixture appHost) : IAsyncLifetime
{
    private const string Provider = "account-guard-test";
    private readonly Mock<IBankConnector> _bank = new();
    private readonly Mock<IAmazonS3> _storage = new();
    private ServiceProvider _services = null!;
    private Guid _userId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await appHost.SeedAsync();
        var connectionString = await appHost.Application.GetConnectionStringAsync("db", Ct);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext, PostgreSqlAppDbContext>(options =>
            options.UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure()));
        services.AddDomainServices();
        services.AddDistributedMemoryCache();
        services.AddHybridCache();
        services.AddSingleton(Mock.Of<ILoginDeletion>());
        services.AddKeyedSingleton(Provider, _bank.Object);
        services.Configure<BankingOptions>(options => options.Provider = Provider);
        services.AddSingleton(_storage.Object);
        services.Configure<ReceiptStorageOptions>(options => options.BucketName = "guard-test");
        _storage.Setup(storage => storage.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { FirstName = "Concurrency test", Email = $"guard-{Guid.NewGuid():N}@test.com" };
        var personal = new Group { Id = user.Id, Name = "Personal" };
        personal.Users.Add(user);
        db.Add(personal);
        await db.SaveChangesAsync(Ct);
        _userId = user.Id;
    }

    private async Task SignIn(IServiceScope scope)
    {
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<User>()
            .SingleAsync(user => user.Id == _userId, Ct);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().Initialize(user);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_bank_link_started_during_deletion_waits_then_refuses_the_stale_signed_in_user()
    {
        await using var deletingScope = _services.CreateAsyncScope();
        await using var linkingScope = _services.CreateAsyncScope();
        await SignIn(linkingScope); // Loaded before deletion: reproduces an in-flight request.
        var db = deletingScope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Add(new BankConnection { UserId = _userId, Provider = Provider, ProviderItemId = $"item-{Guid.NewGuid():N}",
            InstitutionName = "Test bank", LinkedAt = DateTimeOffset.UtcNow,
            AccessTokenCiphertext = deletingScope.ServiceProvider.GetRequiredService<IAccessTokenProtector>().Protect("access-test") });
        await db.SaveChangesAsync(Ct);

        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bank.Setup(bank => bank.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) => { removing.TrySetResult(); await resume.Task.WaitAsync(ct); });
        var deletion = deletingScope.ServiceProvider.GetRequiredService<IAccountService>().DeleteAccount(_userId, Ct);
        await removing.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var link = linkingScope.ServiceProvider.GetRequiredService<IBankConnectionService>()
            .Link(new CreateBankConnectionRequest { PublicToken = "public-test" }, Ct);
        try { Assert.False(link.IsCompleted); }
        finally { resume.TrySetResult(); }

        Assert.Empty(await deletion);
        await Assert.ThrowsAsync<NotFoundException>(() => link);
        _bank.Verify(bank => bank.ExchangeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        await AssertDeletedWithNoPrivateData();
    }

    [Fact(Timeout = 60_000)]
    public async Task Deletion_waits_for_an_upload_then_removes_its_metadata_and_stored_object()
    {
        await using var uploadingScope = _services.CreateAsyncScope();
        await using var deletingScope = _services.CreateAsyncScope();
        await SignIn(uploadingScope);
        await SignIn(deletingScope);
        var expense = await uploadingScope.ServiceProvider.GetRequiredService<ITransactionService>().Create(
            new CreateTransactionRequest { Name = "Test expense", Amount = 12m,
                DateTime = DateTimeOffset.UtcNow, PaidByUserId = _userId }, Ct);
        var putting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _storage.Setup(storage => storage.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (PutObjectRequest _, CancellationToken ct) =>
            { putting.TrySetResult(); await resume.Task.WaitAsync(ct); return new PutObjectResponse(); });
        using var content = new MemoryStream([1, 2, 3]);
        var file = new FormFile(content, 0, content.Length, "file", "receipt.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        var upload = uploadingScope.ServiceProvider.GetRequiredService<IReceiptAttachmentService>().Upload(expense.Id, file, Ct);
        await putting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var deletion = deletingScope.ServiceProvider.GetRequiredService<IAccountService>().DeleteAccount(_userId, Ct);
        try { Assert.False(deletion.IsCompleted); }
        finally { resume.TrySetResult(); }

        await upload;
        Assert.Empty(await deletion);
        await AssertDeletedWithNoPrivateData();
        _storage.Verify(storage => storage.DeleteObjectAsync("guard-test", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_waiting_receipt_upload_rechecks_the_tombstone_before_contacting_storage()
    {
        await using var uploadingScope = _services.CreateAsyncScope();
        await using var deletingScope = _services.CreateAsyncScope();
        await SignIn(uploadingScope);
        var guard = deletingScope.ServiceProvider.GetRequiredService<IAccountDataGuard>();
        var lease = await guard.Acquire(_userId, ct: Ct);
        using var content = new MemoryStream([1, 2, 3]);
        var file = new FormFile(content, 0, content.Length, "file", "receipt.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        var upload = uploadingScope.ServiceProvider.GetRequiredService<IReceiptAttachmentService>().Upload(Guid.NewGuid(), file, Ct);
        try
        {
            Assert.False(upload.IsCompleted);
            var db = deletingScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Set<User>().SingleAsync(user => user.Id == _userId, Ct);
            user.Kind = UserKind.Deleted; user.Email = null; user.FirstName = null;
            await db.SaveChangesAsync(Ct);
        }
        finally { await lease.DisposeAsync(); }
        await Assert.ThrowsAsync<NotFoundException>(() => upload);
        _storage.Verify(storage => storage.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task AssertDeletedWithNoPrivateData()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(UserKind.Deleted, await db.Set<User>().Where(user => user.Id == _userId).Select(user => user.Kind).SingleAsync(Ct));
        Assert.False(await db.Set<BankConnection>().AnyAsync(row => row.UserId == _userId, Ct));
        Assert.False(await db.Set<PendingBankLink>().AnyAsync(row => row.UserId == _userId, Ct));
        Assert.False(await db.Set<ReceiptAttachment>().AnyAsync(row => row.UploadedByUserId == _userId, Ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancelling_a_waiter_does_not_leave_a_session_holding_the_account_lock()
    {
        await using var first = _services.CreateAsyncScope();
        await using var second = _services.CreateAsyncScope();
        await using var third = _services.CreateAsyncScope();
        var lease = await first.ServiceProvider.GetRequiredService<IAccountDataGuard>().Acquire(_userId, ct: Ct);
        try
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var waiting = second.ServiceProvider.GetRequiredService<IAccountDataGuard>().Acquire(_userId, ct: cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { await lease.DisposeAsync(); }
        await using var next = await third.ServiceProvider.GetRequiredService<IAccountDataGuard>().Acquire(_userId, ct: Ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_pending_link_for_a_deleted_owner_is_retired_instead_of_restored()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Set<User>().SingleAsync(user => user.Id == _userId, Ct);
        user.Kind = UserKind.Deleted; user.FirstName = null; user.Email = null;
        var item = new LinkedItem("held-token", "held-item", "Test bank", []);
        var pending = new PendingBankLink { UserId = _userId, Provider = Provider, StartedAt = DateTimeOffset.UtcNow,
            ItemCiphertext = scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>()
                .Protect(JsonSerializer.Serialize(item, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        db.Add(pending);
        await db.SaveChangesAsync(Ct);
        _bank.Setup(bank => bank.RemoveAsync("held-token", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await scope.ServiceProvider.GetRequiredService<IBankConnectionService>().CompletePending(pending.Id, Ct);
        await AssertDeletedWithNoPrivateData();
        _bank.Verify(bank => bank.RemoveAsync("held-token", It.IsAny<CancellationToken>()), Times.Once);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is null) return;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Set<ReceiptAttachment>().Where(row => row.UploadedByUserId == _userId).ExecuteDeleteAsync();
            await db.Set<PendingBankLink>().Where(row => row.UserId == _userId).ExecuteDeleteAsync();
            await db.Set<BankConnection>().Where(row => row.UserId == _userId).ExecuteDeleteAsync();
            await db.Set<TransactionSplit>().Where(row => row.Transaction.GroupId == _userId).ExecuteDeleteAsync();
            await db.Set<Transaction>().Where(row => row.GroupId == _userId).ExecuteDeleteAsync();
            await db.Set<GroupMembership>().IgnoreQueryFilters().Where(row => row.UserId == _userId).ExecuteDeleteAsync();
            await db.Set<Group>().Where(row => row.Id == _userId).ExecuteDeleteAsync();
            await db.Set<User>().Where(row => row.Id == _userId).ExecuteDeleteAsync();
        }
        await _services.DisposeAsync();
    }
}
