namespace GroupSplit.AppHost.Migrations;

public class EFMigrationProjectMetadata : IProjectMetadata
{
    public required string ProjectPath { get; init; }
}