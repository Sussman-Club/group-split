namespace GroupSplit.Seeder.Seeders.Base;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class DependsOnAttribute(Type seederType) : Attribute
{
    public Type SeederType { get; } = seederType;
}