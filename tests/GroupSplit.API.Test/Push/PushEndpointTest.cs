using System.Net;
using System.Net.Http.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Push;

public sealed class PushEndpointTest : IAsyncLifetime
{
    private ApiEndpointHost host = null!;
    public async ValueTask InitializeAsync() => host = await ApiEndpointHost.StartAsync();
    public ValueTask DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task Devices_are_owned_and_each_account_can_have_multiple_devices()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync($"/users/me/push-devices/{first}", new PushDeviceRequest("first", true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync($"/users/me/push-devices/{second}", new PushDeviceRequest("second", true))).StatusCode);
        using var other = host.ClientForAnotherUser();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/users/me/push-devices/{first}", new PushDeviceRequest("stolen", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PutAsJsonAsync($"/users/me/push-devices/{Guid.NewGuid()}", new PushDeviceRequest("first", true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await other.DeleteAsync($"/users/me/push-devices/{first}")).StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Set<PushDevice>().CountAsync());
        Assert.Equal("first", (await db.Set<PushDevice>().FindAsync(first))!.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/users/me/push-devices/{first}")).StatusCode);
        Assert.Equal(1, await db.Set<PushDevice>().CountAsync());
    }

    [Fact]
    public async Task Registration_requires_authentication_and_a_valid_token()
    {
        var route = $"/users/me/push-devices/{Guid.NewGuid()}";
        using var anonymous = host.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(route, new PushDeviceRequest("token", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync(route, new PushDeviceRequest("", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync(route, new PushDeviceRequest(new string('x', 4097), true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync(route, new PushDeviceRequest(new string('x', 2049), true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync(route, new PushDeviceRequest(new string('é', 1025), true))).StatusCode);
    }

    [Fact]
    public async Task Other_categories_can_be_enabled_without_expense_creation()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        var request = new PushDeviceRequest("token", false, true, true, true, true, true);
        (await host.Client.PutAsJsonAsync($"/users/me/push-devices/{id}", request, ct)).EnsureSuccessStatusCode();
        using var scope = host.Services.CreateScope();
        var device = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<PushDevice>().SingleAsync(ct);
        Assert.False(device.ExpensesEnabled);
        Assert.True(device.ExpenseChangesEnabled);
        Assert.True(device.SettlementsEnabled);
        Assert.True(device.PaymentRequestsEnabled);
        Assert.True(device.InvitationsEnabled);
        Assert.True(device.BankSyncIssuesEnabled);
    }

    [Fact]
    public async Task Token_rotation_and_preferences_update_the_same_device()
    {
        var id = Guid.NewGuid();
        var route = $"/users/me/push-devices/{id}";
        (await host.Client.PutAsJsonAsync(route, new PushDeviceRequest("old", true))).EnsureSuccessStatusCode();
        (await host.Client.PutAsJsonAsync(route, new PushDeviceRequest("rotated", false))).EnsureSuccessStatusCode();
        using var scope = host.Services.CreateScope();
        var device = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<PushDevice>().SingleAsync();
        Assert.Equal(id, device.Id);
        Assert.Equal("rotated", device.Token);
        Assert.False(device.ExpensesEnabled);
    }
}
