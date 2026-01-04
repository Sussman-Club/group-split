namespace GroupSplit.AppHost.Seeder;

public class DatabaseAnnotation(IResource resource) : IResourceAnnotation
{
    public IResource Resource { get; } = resource;
}