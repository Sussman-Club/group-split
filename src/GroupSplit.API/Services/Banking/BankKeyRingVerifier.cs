using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Says at startup what is protecting the bank access tokens, and refuses to start when the
/// key ring cannot read the ones already stored.
/// </summary>
/// <remarks>
/// Two things that are really one: whether the ring is wrapped, and whether it still opens.
/// <para>
/// Every check standing between a deployment and its stored tokens asks whether the
/// certificate is <em>there</em> -- the workflow's own <c>require</c>, the AppHost's
/// <c>validate-plaid</c>, and <c>KeyRingExtensions.Load</c>, which goes as far as valid
/// base64, a readable PKCS#12 and a private key. Not one of them asks whether it is the
/// <em>same</em> certificate the ring was wrapped with. Hand the deployment a different but
/// perfectly well-formed one and all three pass, Data Protection cannot unwrap the key that
/// is there, mints a fresh one, and every stored token becomes ciphertext nothing can open.
/// </para>
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
/// It refuses only where a certificate wraps the ring, which is the only place a
/// certificate can be the wrong one. Without one the ring is stored unwrapped, and then an
/// unreadable value is as likely to be the seeder's placeholder -- a token nothing ever
/// protected -- as anything worth stopping for: <c>Unprotect</c> reports a wrong key and a
/// value that was never a payload as the same failure, so there is nothing here that could
/// tell them apart. Both are logged instead.
/// </para>
/// <para>
/// One token is enough to answer the question: they were all written by the same ring. A
/// deployment with no connections yet has nothing to verify and starts, which is also what
/// happens on a host whose database is not there -- a test host, or a first deploy before
/// the migration bundle has run. Absence of tokens is not evidence of a bad key.
/// </para>
/// </remarks>
public static class BankKeyRingVerifier
{
    extension(IHost host)
    {
        /// <summary>
        /// Runs the check, throwing if the ring cannot read what is stored.
        /// </summary>
        /// <remarks>
        /// Called between building the host and running it, rather than from an
        /// <c>IHostedService</c>. Hosted services start in registration order and the one
        /// that starts Kestrel is registered while the builder is constructed -- before any
        /// application code can add its own -- so a check registered as a hosted service
        /// runs with the server already accepting requests. A link that landed in that
        /// window would protect its token with the freshly minted key that this exists to
        /// prevent. Here there is no window.
        /// </remarks>
        public async Task VerifyBankKeyRing(CancellationToken ct = default)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(BankKeyRingVerifier));

            // Read the same way AddBankKeyRing read it, so the two cannot disagree about
            // whether this deployment has one.
            var wrapped = !string.IsNullOrWhiteSpace(
                host.Services.GetRequiredService<IConfiguration>()
                    .GetSection(BankingOptions.SectionName)[nameof(BankingOptions.KeyRingCertificate)]);

            if (!wrapped)
            {
                // Stated out loud, because the failure this guards against is silence: a
                // deployment that forgot the certificate looks exactly like one that has it.
                // Expected in development, where an unwrapped ring is the ordinary posture.
                logger.LogWarning(
                    "No {Section}:{Key} is configured, so the bank access-token key ring is stored unwrapped "
                    + "beside the tokens it opens. Whoever holds a copy of the database holds both. Expected in "
                    + "development; a mistake in a deployment.",
                    BankingOptions.SectionName, nameof(BankingOptions.KeyRingCertificate));
            }

            await using var scope = host.Services.CreateAsyncScope();

            string? ciphertext;

            try
            {
                ciphertext = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                    .Set<BankConnection>()
                    .OrderBy(connection => connection.LinkedAt)
                    .Select(connection => connection.AccessTokenCiphertext)
                    .FirstOrDefaultAsync(ct);
            }
            catch (Exception e)
            {
                // A warning rather than a note. This is the ordinary case on a test host and
                // on a first deploy, but it is also what a deployment looks like when the
                // database was briefly away at startup -- and that one skipped the check
                // silently, which is the shape of failure this whole class exists to end.
                logger.LogWarning(
                    e, "No stored bank connections could be read, so the bank access-token key ring was not "
                       + "checked against one. Expected before the first migration; worth a look otherwise.");

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
            catch (AccessTokenUnreadableException e) when (!wrapped)
            {
                logger.LogWarning(
                    e, "A stored bank access token could not be read. With no certificate configured this is "
                       + "as likely to be seeded development data as a real key problem, so it is not being "
                       + "treated as one.");

                return;
            }
            catch (AccessTokenUnreadableException e)
            {
                throw new InvalidOperationException(
                    "The bank access-token key ring cannot read a token this deployment has already stored. "
                    + $"Either {BankingOptions.SectionName}:{nameof(BankingOptions.KeyRingCertificate)} is not "
                    + "the certificate the ring was wrapped with -- restore the previous one -- or the stored "
                    + "value is not a protected token at all, which is what a hand-edited row looks like. "
                    + "Starting anyway would mint a new key and leave every stored token unreadable, and an "
                    + "item whose token is gone can be neither synced, nor repaired, nor removed at the "
                    + "provider. Reading the ring to find this out will have added a new key to it, which is "
                    + "harmless: the old one is still there and reads again as soon as the right certificate "
                    + "is back.", e);
            }

            logger.LogInformation("The bank access-token key ring reads what is stored.");
        }
    }
}
