using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Configuration;

/// <summary>The URLs a command needs, plus where they came from so errors can say so.</summary>
public sealed record Endpoints
{
    public required Uri Api { get; init; }

    /// <summary>
    /// Null when neither a server origin nor an explicit authority was configured.
    /// <para>
    /// Held separately from <see cref="Authority"/> because most commands never need it:
    /// with <c>GROUPSPLIT_TOKEN</c> set -- the CI and agent path -- nothing ever contacts
    /// the identity server, and demanding its URL up front would fail invocations that
    /// were perfectly well specified.
    /// </para>
    /// </summary>
    public Uri? AuthorityOrNull { get; init; }

    /// <summary>
    /// Where the app is published, when a server origin was configured -- the base a join
    /// link is written against, since the link is a URL a person opens in a browser.
    /// </summary>
    /// <remarks>
    /// Null when the API was pointed at directly with <c>GROUPSPLIT_API_URL</c> and no
    /// origin was given. That configuration is complete for everything else, and it says
    /// nothing at all about where a browser would find the app -- so the token is printed
    /// on its own rather than pasted into a guess.
    /// </remarks>
    public Uri? WebOrNull { get; init; }

    public required string ClientId { get; init; }

    public required string ProfileName { get; init; }

    /// <summary>Human-readable provenance, e.g. "--server" or "GROUPSPLIT_SERVER" or the config path.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Things worth saying about this configuration once, when it is first used. Carried
    /// rather than printed here because resolution happens before there is anywhere to
    /// print to, and because a command that never touches the network should stay silent.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The identity server, or a usable error naming the two ways to supply one.</summary>
    public Uri Authority => AuthorityOrNull ?? throw CliException.Input(
        "No identity server configured.",
        $"Set a server origin with: groupsplit config set server <url>   "
        + $"(or set {EnvironmentVariables.Authority} directly, or {EnvironmentVariables.Token} "
        + "to skip signing in altogether)",
        code: ErrorCodes.ServerNotConfigured);
}
