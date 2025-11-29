namespace GroupSplit.AppHost.EntityFramework;

public class MigrationProjectMetadataAnnotation(string projectPath, string? dbContextTypeName) : IResourceAnnotation
{
    public string? DbContextTypeName => dbContextTypeName;
    public string ProjectPath => projectPath;
}

public sealed class MigrationProjectMetadataAnnotation<TProjectMetadata>(string? dbContextTypeName = null)
    : MigrationProjectMetadataAnnotation(new TProjectMetadata().ProjectPath, dbContextTypeName)
    where TProjectMetadata : IProjectMetadata, new();