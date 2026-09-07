namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The process exit codes, which are part of this CLI's contract: a caller that cannot
/// parse output can still branch on these, and an agent uses them to decide whether to
/// retry, re-authenticate, or ask its user for something.
/// <para>
/// Adding a code is a compatible change. Changing what an existing one means is not --
/// anything already scripted against it silently starts taking the wrong branch.
/// </para>
/// </summary>
public static class ExitCodes
{
    /// <summary>The command did what was asked. Whatever is on stdout is trustworthy.</summary>
    public const int Success = 0;

    /// <summary>Anything that went wrong and is not covered more precisely below.</summary>
    public const int Error = 1;

    /// <summary>No credentials, expired credentials, or the server refused them.</summary>
    public const int AuthRequired = 2;

    /// <summary>The arguments were wrong: bad usage, or a value the server rejected.</summary>
    public const int InvalidInput = 3;

    /// <summary>
    /// A mutation stopped short because it was not confirmed. stdout carries the
    /// confirmation envelope describing what would change.
    /// </summary>
    public const int ConfirmationRequired = 4;
}
