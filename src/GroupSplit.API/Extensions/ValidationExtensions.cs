namespace GroupSplit.API.Extensions;

/// <summary>
/// Turns on minimal-API validation, from inside the assembly the endpoints live in.
/// </summary>
/// <remarks>
/// A one-line wrapper that exists entirely for where it is compiled. In .NET 10 minimal-API
/// validation is a source-generated interceptor on the <c>AddValidation()</c> call site, and
/// what it can see is the compilation that call sits in. Called from a test host in another
/// assembly, it registers the filter and resolves type information for nothing: the filter
/// runs, finds no annotations to check, and the handler is invoked with whatever arrived --
/// so an endpoint test asserting a 400 for a missing required field would have got a 500,
/// and one asserting nothing would have gone green while the annotation was deleted.
/// <para>
/// Called from here, the interceptor is generated in this assembly, alongside
/// <c>MapDomainApi()</c> and everything it routes to. Every host that assembles this API --
/// the deployed one and the test one -- then gets the same validation, which is the whole
/// point of a test host.
/// </para>
/// </remarks>
public static class ValidationExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApiValidation()
        {
            services.AddValidation();

            return services;
        }
    }
}
