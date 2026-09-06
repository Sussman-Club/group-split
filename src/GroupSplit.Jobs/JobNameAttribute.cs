namespace GroupSplit.Jobs;

/// <summary>
/// The name a job type travels under.
/// </summary>
/// <remarks>
/// Required, and deliberately not defaulted to the CLR type name. A job in flight is a
/// string on a queue somewhere, and if the name were the class name then renaming the class
/// -- an ordinary refactor nobody thinks twice about -- would strand every message already
/// sent under the old one. Spelling it out makes the wire name a thing you change on
/// purpose.
/// <para>
/// Prefix by area, as in <c>bank.sync-connection</c>, so a queue's contents read as
/// something rather than as a list of verbs.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class JobNameAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("A job name cannot be blank.", nameof(name))
        : name;
}
