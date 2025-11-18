using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace GroupSplit.API.OpenApi;

public class ExcludePathPrefixOperationProcessor : IOperationProcessor
{
    private readonly string _pathPrefix;

    public ExcludePathPrefixOperationProcessor(string pathPrefix)
    {
        _pathPrefix = pathPrefix;
    }

    public bool Process(OperationProcessorContext context)
    {
        // Return false to exclude the operation
        return !context.OperationDescription.Path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
