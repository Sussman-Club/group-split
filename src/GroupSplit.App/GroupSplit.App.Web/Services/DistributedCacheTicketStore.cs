using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;

namespace GroupSplit.App.Web.Services;

/// <summary>
/// Keeps the authentication ticket on the server so the browser cookie carries
/// only a lookup key.
/// <para>
/// SaveTokens puts the access, refresh and id tokens in the ticket. Left in the
/// cookie those chunk into several kilobytes that ride on every request, and
/// cookies are scoped to the host rather than the port, so they are also sent to
/// everything else sharing localhost. Past Kestrel's 32 KB header limit that
/// surfaces as an HTTP 431.
/// </para>
/// </summary>
internal sealed class DistributedCacheTicketStore(IDistributedCache cache) : ITicketStore
{
    private const string KeyPrefix = "gs-auth-ticket:";

    /// <summary>Fallback lifetime for a ticket that carries no absolute expiry.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Guid.NewGuid().ToString("N");

        await RenewAsync(key, ticket);

        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new DistributedCacheEntryOptions();

        if (ticket.Properties.ExpiresUtc is { } expiresUtc)
        {
            options.SetAbsoluteExpiration(expiresUtc);
        }
        else
        {
            options.SetSlidingExpiration(DefaultLifetime);
        }

        await cache.SetAsync(key, TicketSerializer.Default.Serialize(ticket), options);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var payload = await cache.GetAsync(key);

        return payload is null ? null : TicketSerializer.Default.Deserialize(payload);
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(key);
}
