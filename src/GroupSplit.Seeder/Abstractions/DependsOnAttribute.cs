namespace GroupSplit.Seeder.Abstractions;

/// <summary>
///     Specifies that a seeder depends on another seeder.
///     Used to establish execution order when running multiple seeders.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class DependsOnAttribute(Type seederType) : Attribute
{
    /// <summary>
    ///     Gets the type of the seeder that this seeder depends on.
    /// </summary>
    public Type SeederType { get; } = seederType;
}