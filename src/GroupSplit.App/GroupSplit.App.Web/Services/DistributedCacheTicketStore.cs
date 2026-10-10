using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
/// <para>
/// Encrypted with the web app's Data Protection ring, as the cookie it replaces was. The
/// ticket is who somebody is, and the cache is Redis, which snapshots to a volume. A ticket
/// the ring cannot open -- stored in plain text before this, or under a lost key -- is
/// answered as missing, so the cookie naming it simply stops signing anybody in.
/// </para>
/// </summary>
internal sealed class DistributedCacheTicketStore(
    IDistributedCache cache,
    IDataProtectionProvider dataProtection,
    ILogger<DistributedCacheTicketStore> logger) : ITicketStore
{
    private const string KeyPrefix = "gs-auth-ticket:";

    /// <summary>The purpose tickets are protected under. Changing it signs everybody out.</summary>
    internal const string ProtectorPurpose = "GroupSplit.App.Web.AuthTickets.v1";

    private readonly IDataProtector _protector = dataProtection.CreateProtector(ProtectorPurpose);

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

        await cache.SetAsync(key, _protector.Protect(TicketSerializer.Default.Serialize(ticket)), options);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var payload = await cache.GetAsync(key);

        if (payload is null)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(payload));
        }
        catch (CryptographicException)
        {
            logger.LogInformation("A stored sign-in ticket could not be unprotected and is being discarded.");

            await cache.RemoveAsync(key);

            return null;
        }
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(key);
}
