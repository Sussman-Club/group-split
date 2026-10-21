using GroupSplit.API.Services;
using GroupSplit.API.Services.Push;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using UserEntity = GroupSplit.Data.Entities.User;
using GroupEntity = GroupSplit.Data.Entities.Group;

namespace GroupSplit.API.Test.Push;

public sealed class ExpensePushQueueTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Creating_an_expense_queues_every_enabled_device_of_other_current_members()
    {
        var actor = GetService<ICurrentUser>().User;
        var recipient = new UserEntity { FirstName = "Member" };
        var outsider = new UserEntity { FirstName = "Outsider" };
        var departed = new UserEntity { FirstName = "Departed" };
        var group = new GroupEntity { Name = "Trip" };
        group.Users.Add(actor);
        group.Users.Add(recipient);
        group.Users.Add(departed);
        DbContext.AddRange(group, outsider);
        await DbContext.SaveChangesAsync();
        var membership = await DbContext.Set<GroupMembership>().SingleAsync(m => m.GroupId == group.Id && m.UserId == departed.Id);
        membership.LeftAt = DateTimeOffset.UtcNow;

        PushDevice Device(UserEntity user, string token, bool enabled = true, bool stale = false) => new()
        {
            UserId = user.Id, Token = token, ExpensesEnabled = enabled,
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(stale ? -31 : 0)
        };
        var first = Device(recipient, "phone");
        var second = Device(recipient, "tablet");
        DbContext.AddRange(first, second, Device(recipient, "disabled", false), Device(recipient, "stale", stale: true),
            Device(actor, "actor"), Device(outsider, "outsider"), Device(departed, "departed"));
        await DbContext.SaveChangesAsync();

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "Lunch", Amount = 12, DateTime = DateTimeOffset.UtcNow
        });
        var queued = await DbContext.Set<PushDelivery>().ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), queued.Select(d => d.DeviceId).Order());
        Assert.All(queued, d => { Assert.Equal(recipient.Id, d.UserId); Assert.Equal(group.Id, d.GroupId); });
    }
}
