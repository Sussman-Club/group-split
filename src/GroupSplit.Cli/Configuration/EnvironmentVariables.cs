namespace GroupSplit.Cli.Configuration;

/// <summary>
/// Every environment variable this CLI reads, in one list so `groupsplit config list`
/// and the docs cannot fall out of step with the code.
/// </summary>
public static class EnvironmentVariables
{
    public const string Server = "GROUPSPLIT_SERVER";
    public const string ApiUrl = "GROUPSPLIT_API_URL";
    public const string Authority = "GROUPSPLIT_AUTHORITY";
    public const string ClientId = "GROUPSPLIT_CLIENT_ID";
    public const string Profile = "GROUPSPLIT_PROFILE";

    /// <summary>
    /// A bearer token used as-is, skipping the stored credentials and the device flow.
    /// This is how a CI job or an agent authenticates: no browser, no interactive step,
    /// nothing written to disk.
    /// </summary>
    public const string Token = "GROUPSPLIT_TOKEN";

    /// <summary>Set to anything to attach a stack trace to unexpected-error envelopes.</summary>
    public const string Debug = "GROUPSPLIT_DEBUG";

    public const string ConfigHome = "XDG_CONFIG_HOME";
    public const string DataHome = "XDG_DATA_HOME";
}
