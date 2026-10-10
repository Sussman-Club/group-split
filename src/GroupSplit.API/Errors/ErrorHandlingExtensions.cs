namespace GroupSplit.API.Errors;

/// <summary>
/// The error contract's wiring, in one pair so <c>Program.cs</c> and the endpoint test host
/// cannot drift apart: the tests would otherwise be exercising a pipeline that maps
/// exceptions differently from the one that ships.
/// </summary>
public static class ErrorHandlingExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApiErrorHandling()
        {
            services.AddProblemDetails(options =>
                options.CustomizeProblemDetails = context =>
                    Problems.Enrich(context.HttpContext, context.ProblemDetails));

            // In registration order; the first to return true wins.
            services.AddExceptionHandler<DomainExceptionHandler>();
            services.AddExceptionHandler<UnhandledExceptionHandler>();

            return services;
        }
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        /// <summary>
        /// The two problems every authenticated endpoint can answer with whatever it does:
        /// the 401 for a missing or expired token, and the 500 for a bug. Declared once per
        /// route group so the generated client deserializes both into problem details
        /// rather than treating them as unexpected status codes.
        /// </summary>
        public TBuilder ProducesStandardProblems() =>
            builder
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// First in the pipeline, so that it sees exceptions from every middleware after
        /// it, and so that a bare status code from any of them -- the 401 the bearer
        /// handler writes, the 404 routing writes -- is given a problem body on the way
        /// out.
        /// </summary>
        public IApplicationBuilder UseApiErrorHandling()
        {
            app.UseExceptionHandler();
            app.UseStatusCodePages();

            return app;
        }
    }
}
