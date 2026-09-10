using GroupSplit.Shared;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Banking.Plaid;

/// <summary>
/// Refuses a <see cref="PlaidConnectorOptions.RedirectUri"/> that this deployment could not
/// actually come back to.
/// </summary>
/// <remarks>
/// The redirect address is the one value in the linking flow that two parties have to agree
/// on in advance and cannot check with each other: it is registered in Plaid's dashboard,
/// sent up with every link token, and served by the web app. Get it wrong in the dashboard
/// and Plaid refuses the token; get it wrong here and Plaid refuses the token; point it
/// somewhere this app does not serve and the person is returned to a page that cannot
/// finish what they started — with their bank believing it is connected.
/// <para>
/// Checked when the value is bound, because all three of those belong to a deployment
/// rather than to a caller, and the alternative is finding out from the first person who
/// tries to link an OAuth bank.
/// </para>
/// <para>
/// Absent stays valid, and is the ordinary case: without it Plaid Link runs OAuth in a
/// popup and never leaves the page, which is what every deployment does today.
/// </para>
/// <para>
/// Two things are checked, and the path is the weaker of them. A path is all this could
/// check on its own, and a path alone says nothing about whose page it is:
/// <c>https://somewhere-else.example.com/bank/oauth</c> satisfies it exactly as well as
/// this deployment's own address. What names the deployment is
/// <see cref="BankingOptions.PublicOrigin"/> -- the same origin the web app is served on
/// and the one a provider is already told to deliver webhooks to -- so where that is set,
/// the redirect has to be on it. Where it is not, only the path can be checked, and the
/// message says so rather than implying more than it knows.
/// </para>
/// </remarks>
internal sealed class PlaidConnectorOptionsValidator(IOptions<BankingOptions> banking)
    : IValidateOptions<PlaidConnectorOptions>
{
    private static string Setting =>
        $"{PlaidConnectorOptions.SectionName}:{nameof(PlaidConnectorOptions.RedirectUri)}";

    private static string OriginSetting =>
        $"{BankingOptions.SectionName}:{nameof(BankingOptions.PublicOrigin)}";

    public ValidateOptionsResult Validate(string? name, PlaidConnectorOptions options)
    {
        if (options.RedirectUri is not { } configured || string.IsNullOrWhiteSpace(configured))
            return ValidateOptionsResult.Success;

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri))
            return ValidateOptionsResult.Fail(
                $"{Setting} is not an absolute URL. Write the origin scheme first, "
                + $"e.g. https://groupsplit.example.com{BankLinkAddresses.OAuthReturnPath}.");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail(
                $"{Setting} is {uri.Scheme} rather than https. Plaid refuses to register a redirect "
                + "address that is not HTTPS, so no OAuth bank could be linked at all.");

        // Plaid matches the registered address exactly and refuses either of these on it.
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return ValidateOptionsResult.Fail(
                $"{Setting} carries a query or fragment. Plaid registers the address exactly as "
                + "written and will not accept one.");

        // The one thing only this application knows. A deployment is free to serve the app
        // under a prefix, so the path has to end with ours rather than be it.
        if (!uri.AbsolutePath.TrimEnd('/').EndsWith(BankLinkAddresses.OAuthReturnPath, StringComparison.Ordinal))
            return ValidateOptionsResult.Fail(
                $"{Setting} is {uri.AbsolutePath}, which this application does not serve. The page that "
                + $"finishes an interrupted sign-in is at {BankLinkAddresses.OAuthReturnPath}, so the value "
                + $"is the public origin followed by that -- and the same address has to be registered in "
                + "the Plaid dashboard.");

        // And whose page it is, where this deployment has said. Compared as an origin --
        // scheme, host and port -- because that is what "somewhere this app serves" means
        // and it is all the two settings have to agree on; the path above is the rest.
        if (banking.Value.PublicOrigin is { Length: > 0 } configuredOrigin
            && Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin)
            && !SameOrigin(uri, origin))
        {
            return ValidateOptionsResult.Fail(
                $"{Setting} is on {uri.GetLeftPart(UriPartial.Authority)}, which is not this deployment: "
                + $"{OriginSetting} is {origin.GetLeftPart(UriPartial.Authority)}. A person signing in at "
                + "an OAuth bank would be returned to somebody else's page, with their bank believing it "
                + "is connected. The value is that origin followed by "
                + $"{BankLinkAddresses.OAuthReturnPath}.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool SameOrigin(Uri redirect, Uri origin) =>
        string.Equals(redirect.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(redirect.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
        && redirect.Port == origin.Port;
}
