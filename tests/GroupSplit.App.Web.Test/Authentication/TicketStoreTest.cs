using System.Security.Claims;
using System.Text;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// The sign-in ticket, which the cookie only names and Redis holds.
/// </summary>
public class TicketStoreTest
{
    private readonly MemoryDistributedCache _cache =
        new(new OptionsWrapper<MemoryDistributedCacheOptions>(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task A_ticket_survives_a_round_trip()
    {
        var store = Build();

        var key = await store.StoreAsync(Ticket());
        var read = await store.RetrieveAsync(key);

        Assert.Equal("anabel@test.com", read?.Principal.FindFirstValue(ClaimTypes.Email));
    }

    /// <summary>
    /// The ticket is who somebody is, and Redis snapshots it to a volume. It used to be
    /// written there exactly as the serializer produced it, claims readable in the bytes.
    /// </summary>
    [Fact]
    public async Task What_is_written_to_the_cache_is_not_the_ticket_in_plain_text()
    {
        var key = await Build().StoreAsync(Ticket());

        var written = Encoding.UTF8.GetString((await _cache.GetAsync(key, TestContext.Current.CancellationToken))!);

        Assert.DoesNotContain("anabel@test.com", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every ticket stored at the moment this deploys is in plain text. Its cookie stops
    /// signing anybody in -- the person signs in again -- rather than failing the request.
    /// </summary>
    [Fact]
    public async Task A_ticket_written_in_plain_text_is_answered_as_missing_and_dropped()
    {
        const string key = "gs-auth-ticket:legacy";

        await _cache.SetAsync(key, TicketSerializer.Default.Serialize(Ticket()), TestContext.Current.CancellationToken);

        Assert.Null(await Build().RetrieveAsync(key));
        Assert.Null(await _cache.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_ticket_another_key_ring_wrote_is_answered_as_missing()
    {
        var key = await Build(new EphemeralDataProtectionProvider()).StoreAsync(Ticket());

        Assert.Null(await Build(new EphemeralDataProtectionProvider()).RetrieveAsync(key));
    }

    private DistributedCacheTicketStore Build(IDataProtectionProvider? protection = null) =>
        new(_cache, protection ?? _protection, NullLogger<DistributedCacheTicketStore>.Instance);

    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    private static AuthenticationTicket Ticket() =>
        new(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Email, "anabel@test.com")],
                CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            CookieAuthenticationDefaults.AuthenticationScheme);
}
