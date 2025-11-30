namespace GroupSplit.AppHost.EntityFramework;

public class MigrationOrchestrationBuilder<TDatabaseResource>(IResourceBuilder<TDatabaseResource> dbResourceBuilder)
    where TDatabaseResource : IResourceWithConnectionString, IResourceWithParent
{
    public IResourceBuilder<TDatabaseResource> DbResourceBuilder => dbResourceBuilder;
}