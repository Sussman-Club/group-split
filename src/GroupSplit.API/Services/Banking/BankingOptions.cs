namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Which provider this deployment links banks through.
/// </summary>
/// <remarks>
/// There is no <c>Enabled</c> flag here on purpose. Whether bank sync works is whether a
/// connector is registered for <see cref="Provider"/>, and that registration is what the
/// deployment's <c>plaid-enabled</c> switch controls. A second flag could disagree with the
/// first, and the pair would then have to be kept in step by whoever remembered.
/// </remarks>
public sealed class BankingOptions
{
    public const string SectionName = "Banking";

    /// <summary>
    /// The key of the connector to link new banks through. Existing connections are synced
    /// through whichever provider they were made with, so changing this does not strand
    /// them.
    /// </summary>
    public string Provider { get; set; } = "plaid";

    /// <summary>
    /// The origin bank providers reach this deployment on, scheme included, e.g.
    /// <c>https://groupsplit.example.com</c>.
    /// </summary>
    /// <remarks>
    /// Given rather than read off the request, because this value is handed to a provider as
    /// the address to deliver webhooks to and the request's host is whatever the caller put in
    /// the Host header: a caller who could choose it would point the provider at their own
    /// server and be sent everything that happens to somebody else's bank. Behind a proxy the
    /// request could not answer it anyway -- TLS is terminated upstream, so the scheme it
    /// arrives on is plain HTTP.
    /// <para>
    /// Absent means no webhook address is given at all, which is the ordinary case locally:
    /// nothing outside could reach a development machine, and a sync runs on linking, on
    /// demand, and nightly regardless. A deployment always sets it, from the same public
    /// origin the rest of the stack is served on, and
    /// <see cref="BankingOptionsValidator"/> refuses a value a provider would not accept.
    /// </para>
    /// </remarks>
    public string? PublicOrigin { get; set; }

    /// <summary>
    /// A PKCS#12 certificate, base64 encoded, that the Data Protection key ring is
    /// encrypted with. Absent means the ring is stored unwrapped.
    /// </summary>
    /// <remarks>
    /// The one thing standing between a database dump and the bank access tokens in it,
    /// which is why it is a deployment secret and must never be stored beside the database.
    /// Absent is the ordinary case in development and a mistake in a deployment, so the
    /// publish refuses it when bank sync is on.
    /// <para>
    /// Losing it loses the stored tokens and nothing else; everybody links their bank again.
    /// Rotating it is <c>UnprotectKeysWithAnyCertificate</c> with both, which is not wired
    /// up yet because there is nothing to rotate away from.
    /// </para>
    /// </remarks>
    public string? KeyRingCertificate { get; set; }
}
