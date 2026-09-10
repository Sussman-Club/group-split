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
/// </remarks>
internal sealed class PlaidConnectorOptionsValidator : IValidateOptions<PlaidConnectorOptions>
{
    private static string Setting =>
        $"{PlaidConnectorOptions.SectionName}:{nameof(PlaidConnectorOptions.RedirectUri)}";

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

        return ValidateOptionsResult.Success;
    }
}
