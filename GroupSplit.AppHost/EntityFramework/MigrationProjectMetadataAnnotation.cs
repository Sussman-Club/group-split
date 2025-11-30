namespace GroupSplit.AppHost.EntityFramework;

public class MigrationProjectMetadataAnnotation(string projectPath, string? dbContextTypeName) : IResourceAnnotation
{
    public string? DbContextTypeName => dbContextTypeName;
    public string ProjectPath => projectPath;
}