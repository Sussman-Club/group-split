using GroupSplit.API.Services;
using GroupSplit.API.Services.Push;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Moq;
using UserEntity = GroupSplit.Data.Entities.User;
using GroupEntity = GroupSplit.Data.Entities.Group;

namespace GroupSplit.API.Test.Push;

public sealed class PushCategoriesTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(PushNotificationKind.ExpenseChanged)]
    [InlineData(PushNotificationKind.Settlement)]
    [InlineData(PushNotificationKind.PaymentRequest)]
    [InlineData(PushNotificationKind.InvitationAccepted)]
    public async Task Categories_stage_only_selected_current_recipient_devices(PushNotificationKind kind)
    {
        var actor = GetService<ICurrentUser>().User;
        var member = new UserEntity { FirstName = "Recipient" };
        var another = new UserEntity { FirstName = "Other member" };
        var outsider = new UserEntity { FirstName = "Outside" };
        var group = new GroupEntity { Name = "Trip", Users = { actor, member, another } };
        DbContext.AddRange(group, outsider);
        PushDevice Device(UserEntity user, string token, bool enabled) => new()
        {
            UserId = user.Id, Token = token, UpdatedAt = DateTimeOffset.UtcNow,
            ExpensesEnabled = enabled, SettlementsEnabled = enabled,
            InvitationsEnabled = enabled
        };
        var expected = Device(member, "recipient", true);
        DbContext.AddRange(expected, Device(member, "disabled", false), Device(actor, "actor", true),
            Device(another, "other", true), Device(outsider, "outside", true));
        await DbContext.SaveChangesAsync(Ct);

        await GetService<PushNotificationQueue>().ForGroup(kind, group.Id, actor.Id, Ct,
            recipients: [member.Id]);
        var staged = Assert.Single(DbContext.ChangeTracker.Entries<BackgroundJob>());
        Assert.Equal(EntityState.Added, staged.State);
        var job = System.Text.Json.JsonSerializer.Deserialize<SendPushNotification>(staged.Entity.Payload)!;
        Assert.Equal(expected.Id, job.DeviceId);
        Assert.Equal(kind, job.Kind);
        Assert.DoesNotContain("recipient", staged.Entity.Payload);
    }

    [Theory]
    [InlineData(PushNotificationKind.ExpenseChanged)]
    [InlineData(PushNotificationKind.Settlement)]
    [InlineData(PushNotificationKind.InvitationAccepted)]
    public async Task Delivery_rechecks_category_preference_and_current_membership(PushNotificationKind kind)
    {
        var user = GetService<ICurrentUser>().User;
        var group = new GroupEntity { Name = "Trip", Users = { user } };
        var device = new PushDevice { UserId = user.Id, Token = "token", UpdatedAt = DateTimeOffset.UtcNow,
            ExpensesEnabled = true, SettlementsEnabled = true, InvitationsEnabled = true };
        DbContext.AddRange(group, device);
        await DbContext.SaveChangesAsync(Ct);
        var job = new SendPushNotification(device.Id, user.Id, group.Id, Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1), kind);
        var sender = new Mock<IPushSender>();
        sender.Setup(s => s.SendAsync("token", group.Id, "Trip", job.DeliveryId, It.IsAny<CancellationToken>(), kind, null))
            .ReturnsAsync(true);
        var handler = new SendPushNotificationHandler(DbContext, sender.Object, TimeProvider.System);
        await handler.HandleAsync(job, Ct);
        sender.Verify(s => s.SendAsync("token", group.Id, "Trip", job.DeliveryId, It.IsAny<CancellationToken>(), kind, null), Times.Once);
        sender.Invocations.Clear();

        var membership = await DbContext.Set<GroupMembership>().SingleAsync(m => m.GroupId == group.Id && m.UserId == user.Id, Ct);
        membership.LeftAt = DateTimeOffset.UtcNow;
        await DbContext.SaveChangesAsync(Ct);
        await handler.HandleAsync(job, Ct);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Expense_changes_payment_requests_and_settlements_stage_jobs_with_the_save()
    {
        var actor = GetService<ICurrentUser>().User;
        var recipient = new UserEntity { FirstName = "Recipient" };
        var group = new GroupEntity { Name = "Trip", Users = { actor, recipient } };
        var device = new PushDevice { UserId = recipient.Id, Token = "token", UpdatedAt = DateTimeOffset.UtcNow,
            ExpensesEnabled = true, SettlementsEnabled = true };
        DbContext.AddRange(group, device);
        await DbContext.SaveChangesAsync(Ct);
        var expenses = GetService<ITransactionService>();
        var expense = await expenses.Create(new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "Lunch", Amount = 12, DateTime = DateTimeOffset.UtcNow
        }, Ct);
        var update = (await expenses.GetUpdateModel(expense.Id, Ct))!;
        update.Name = "Renamed lunch";
        await expenses.Update(expense.Id, update, Ct);
        await GetService<ISettlementRequestService>().CreateForGroup(group.Id,
            new CreateSettlementRequest { RecipientUserId = recipient.Id, Amount = 6 }, Ct);
        await GetService<ISettlementService>().RecordRepayment(group.Id,
            new RecordRepaymentRequest { FromUserId = recipient.Id, ToUserId = actor.Id, Amount = 6 }, Ct);
        var jobs = await DbContext.Set<BackgroundJob>().Where(j => j.Type == SendPushNotification.Name).ToListAsync(Ct);
        var kinds = jobs.Select(j => System.Text.Json.JsonSerializer.Deserialize<SendPushNotification>(j.Payload)!.Kind).Order();
        Assert.Equal(new[] { PushNotificationKind.ExpenseChanged, PushNotificationKind.Settlement,
            PushNotificationKind.PaymentRequest }.Order(), kinds);
    }

    [Fact]
    public async Task Resolved_payment_request_is_not_sent()
    {
        var recipient = GetService<ICurrentUser>().User;
        var requester = new UserEntity { FirstName = "Requester" };
        var group = new GroupEntity { Name = "Trip", Users = { requester, recipient } };
        var payment = new SettlementRequest { Requester = requester, Recipient = recipient, Token = "opaque-token",
            Currency = "USD", Amount = 1, CreatedAt = DateTimeOffset.UtcNow, ResolvedAt = DateTimeOffset.UtcNow };
        payment.Groups.Add(new SettlementRequestGroup { Group = group, DebtAtCreation = 1, RequestedAmount = 1 });
        var device = new PushDevice { UserId = recipient.Id, Token = "token", SettlementsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow };
        DbContext.AddRange(group, payment, device);
        await DbContext.SaveChangesAsync(Ct);
        var sender = new Mock<IPushSender>();
        await new SendPushNotificationHandler(DbContext, sender.Object, TimeProvider.System).HandleAsync(
            new SendPushNotification(device.Id, recipient.Id, group.Id, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1),
                PushNotificationKind.PaymentRequest, payment.Id), Ct);
        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Bank_issue_is_private_to_its_owner_and_skipped_after_repair()
    {
        var owner = GetService<ICurrentUser>().User;
        var other = new UserEntity { FirstName = "Other" };
        var bank = new BankConnection { User = owner, Provider = "test", ProviderItemId = "item",
            InstitutionName = "Test bank", AccessTokenCiphertext = "encrypted", LinkedAt = DateTimeOffset.UtcNow,
            Status = BankConnectionStatus.LoginRequired };
        var device = new PushDevice { UserId = owner.Id, Token = "token", BankSyncIssuesEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow };
        var otherDevice = new PushDevice { UserId = other.Id, Token = "other-token", BankSyncIssuesEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow };
        DbContext.AddRange(bank, other, device, otherDevice);
        await DbContext.SaveChangesAsync(Ct);
        await GetService<PushNotificationQueue>().ForBankIssue(owner.Id, bank.Id, Ct);
        var queued = Assert.Single(DbContext.ChangeTracker.Entries<BackgroundJob>());
        var job = System.Text.Json.JsonSerializer.Deserialize<SendPushNotification>(queued.Entity.Payload)!;
        Assert.Equal(device.Id, job.DeviceId);
        var sender = new Mock<IPushSender>();
        sender.Setup(s => s.SendAsync("token", Guid.Empty, "", job.DeliveryId, It.IsAny<CancellationToken>(),
            PushNotificationKind.BankSyncIssue, null)).ReturnsAsync(true);
        var handler = new SendPushNotificationHandler(DbContext, sender.Object, TimeProvider.System);
        await handler.HandleAsync(job, Ct);
        sender.Invocations.Clear();
        bank.Status = BankConnectionStatus.Active;
        await DbContext.SaveChangesAsync(Ct);
        await handler.HandleAsync(job, Ct);
        await handler.HandleAsync(job with { DeviceId = otherDevice.Id, UserId = other.Id }, Ct);
        sender.VerifyNoOtherCalls();
    }
}
