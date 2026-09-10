namespace GroupSplit.Shared;

/// <summary>
/// Addresses in the linking flow that are fixed rather than discovered.
/// </summary>
/// <remarks>
/// Here, in the contract both ends share, because these are the one part of the flow the
/// two ends cannot agree on at runtime: a provider is told the address up front and
/// registers it, so nothing can negotiate it later. The API hands it out, the web app
/// serves it, and a mismatch is not a degraded flow — the provider refuses to open a
/// session at all.
/// </remarks>
public static class BankLinkAddresses
{
    /// <summary>
    /// The path a bank's own sign-in page sends the person back to, appended to the
    /// deployment's public origin.
    /// </summary>
    /// <remarks>
    /// Served by <c>Pages/BankOAuthReturn.razor</c>, whose <c>@page</c> directive has to
    /// repeat the literal — a routing attribute cannot read a constant. The validator on
    /// the Plaid options is what keeps a deployment from registering a different one.
    /// </remarks>
    public const string OAuthReturnPath = "/bank/oauth";
}
