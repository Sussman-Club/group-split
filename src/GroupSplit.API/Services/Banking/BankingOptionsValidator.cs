using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Refuses a <see cref="BankingOptions.PublicOrigin"/> no provider could deliver to.
/// </summary>
/// <remarks>
/// Checked when the value is bound rather than when somebody links a bank, because the
/// mistake it catches belongs to a deployment and not to a caller. Providers refuse a webhook
/// address that is not HTTPS, so a wrong value here is not a degraded link flow -- it is
/// every link attempt answered 502, found by the first person who tries to link a bank rather
/// than by the deploy that caused it.
/// <para>
/// Absent stays valid. That is the development posture, where the origin the request arrived
/// on stands in.
/// </para>
/// </remarks>
internal sealed class BankingOptionsValidator : IValidateOptions<BankingOptions>
{
    private static string Setting => $"{BankingOptions.SectionName}:{nameof(BankingOptions.PublicOrigin)}";

    public ValidateOptionsResult Validate(string? name, BankingOptions options)
    {
        if (options.PublicOrigin is not { } origin || string.IsNullOrWhiteSpace(origin))
            return ValidateOptionsResult.Success;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return ValidateOptionsResult.Fail(
                $"{Setting} is not an absolute URL. Write the origin scheme first, "
                + "e.g. https://groupsplit.example.com.");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail(
                $"{Setting} is {uri.Scheme} rather than https. Bank providers refuse a webhook "
                + "address that is not HTTPS, so no bank could be linked at all.");

        // A path here would land the webhook route under it, and it is served at the origin.
        // Query and fragment are meaningless in an origin and mean the value is something
        // else that was pasted in -- a full link-token URL, say.
        if (uri.PathAndQuery is not "/" || uri.Fragment.Length > 0)
            return ValidateOptionsResult.Fail(
                $"{Setting} carries a path, query or fragment. It is an origin, "
                + "e.g. https://groupsplit.example.com, and the webhook path is appended to it.");

        return ValidateOptionsResult.Success;
    }
}
