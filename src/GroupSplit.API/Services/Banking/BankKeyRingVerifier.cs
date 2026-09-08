using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Refuses to start when the key ring cannot read the access tokens already stored.
/// </summary>
/// <remarks>
/// Every check standing between a deployment and its stored tokens asks whether the
/// certificate is <em>there</em> -- the workflow's own <c>require</c>, the AppHost's
/// <c>validate-plaid</c>, and <c>KeyRingExtensions.Load</c>, which goes as far as valid
/// base64, a readable PKCS#12 and a private key. Not one of them asks whether it is the
/// <em>same</em> certificate the ring was wrapped with. Hand the deployment a different but
/// perfectly well-formed one and all three pass, Data Protection cannot unwrap the key that
/// is there, mints a fresh one, and every stored token becomes ciphertext nothing can open.
/// <para>
/// That is worth refusing to start over, because of what an unreadable token costs. A
/// provider item whose token is gone can no longer be synced, no longer be repaired in
/// update mode -- which needs the token to name the item it is repairing -- and no longer
/// be removed, because <c>/item/remove</c> takes the token too. It is stranded at the
/// provider, still counted against whatever the plan counts, with nothing left on this side
/// able to retire it. On a plan whose item allowance is spent once and never returned, each
/// one is permanent.
/// </para>
/// <para>
/// One token is enough to answer the question: they were all written by the same ring. A
/// deployment with no connections yet has nothing to verify and starts, which is also what
/// happens on a host whose database is not there -- a test host, or a first deploy before
/// the migration bundle has run. Absence of tokens is not evidence of a bad key.
/// </para>
/// </remarks>
internal sealed class BankKeyRingVerifier(
    IServiceScopeFactory scopes,
    ILogger<BankKeyRingVerifier> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();

        string? ciphertext;

        try
        {
            ciphertext = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Set<BankConnection>()
                .OrderBy(connection => connection.LinkedAt)
                .Select(connection => connection.AccessTokenCiphertext)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogDebug(
                e, "No stored bank connections could be read, so the key ring was not checked against one.");

            return;
        }

        if (ciphertext is null)
        {
            return;
        }

        try
        {
            scope.ServiceProvider.GetRequiredService<IAccessTokenProtector>().Unprotect(ciphertext);
        }
        catch (AccessTokenUnreadableException e)
        {
            throw new InvalidOperationException(
                "The bank access-token key ring cannot read a token this deployment has already stored, "
                + $"so {BankingOptions.SectionName}:{nameof(BankingOptions.KeyRingCertificate)} is not the "
                + "certificate the ring was wrapped with. Restore the previous one. Starting anyway would "
                + "mint a new key and leave every stored token unreadable, and an item whose token is gone "
                + "can be neither synced, nor repaired, nor removed at the provider.", e);
        }

        logger.LogInformation("The bank access-token key ring reads what is stored.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
