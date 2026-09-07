namespace GroupSplit.API.Services.Banking.Plaid;

/// <summary>
/// What this application tells Plaid about itself. The credentials live in Going.Plaid's
/// own <c>Plaid</c> section beside these.
/// </summary>
public sealed class PlaidConnectorOptions
{
    public const string SectionName = "Plaid";

    /// <summary>The name shown to people inside Plaid's linking UI.</summary>
    public string ClientName { get; set; } = "Group Split";

    /// <summary>
    /// How much history to ask for on a first sync. Ninety days is Plaid's default and
    /// arrives quickly; more takes longer to become available and the app has nothing to do
    /// with it yet.
    /// </summary>
    public int DaysRequested { get; set; } = 90;

    /// <summary>
    /// Where a bank's own sign-in page sends somebody back to, for the institutions that
    /// take them away from the app. Must be registered in the Plaid dashboard first, so it
    /// stays unset until somebody has done that.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// The countries whose institutions to offer, as ISO 3166-1 alpha-2.
    /// </summary>
    public string[] CountryCodes { get; set; } = ["US"];
}
