namespace GroupSplit.Seeder;

public sealed class SeederOptions
{
    public required SeederPaths Paths { get; init; }
}

public sealed class SeederPaths
{
    public required string Groups { get; init; }
    public required string Users { get; init; }
    public required string IdentityUsers { get; init; }
}