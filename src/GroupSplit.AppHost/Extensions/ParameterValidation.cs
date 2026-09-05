using Aspire.Hosting.Pipelines;

namespace GroupSplit.AppHost.Extensions;

#pragma warning disable ASPIREPIPELINES001

/// <summary>
/// Checks that only make sense once parameter values exist.
/// <para>
/// The AppHost declares parameters and passes them through; it never reads their values
/// while building the model, which is what Aspire asks of it. A rule that spans several
/// values -- a feature that is switched on has to have its credentials -- therefore runs
/// as a pipeline step: after <see cref="WellKnownPipelineSteps.ProcessParameters"/> has
/// resolved the values, and before <see cref="WellKnownPipelineSteps.BuildPrereq"/>, which
/// every image build waits on, so a bad deployment fails before a single image is built.
/// That also keeps the rules out of run mode, where the values are local defaults and
/// nothing is being shipped.
/// </para>
/// </summary>
internal static class ParameterValidation
{
    extension(IResourceBuilder<ParameterResource> flag)
    {
        /// <summary>
        /// Fails the pipeline when <paramref name="flag"/> is <c>true</c> and any of
        /// <paramref name="required"/> resolves to an empty value.
        /// </summary>
        public async Task RequireValuesWhenEnabledAsync(
            PipelineStepContext context,
            IReadOnlyList<IResourceBuilder<ParameterResource>> required)
        {
            if (!await flag.IsTrueAsync(context.CancellationToken))
                return;

            var missing = new List<string>();

            foreach (var parameter in required)
            {
                var value = await parameter.Resource.GetValueAsync(context.CancellationToken);

                if (string.IsNullOrWhiteSpace(value))
                    missing.Add(parameter.Resource.Name);
            }

            if (missing.Count == 0)
                return;

            throw new InvalidOperationException(
                $"{flag.Resource.Name} is true but {string.Join(", ", missing)} "
                + (missing.Count == 1 ? "has" : "have")
                + $" no value. Set {(missing.Count == 1 ? "it" : "them")}, or set {flag.Resource.Name} to false.");
        }

        /// <summary>
        /// Reads a true/false parameter, refusing anything else: a flag that is silently
        /// treated as false would hide a typo in a deployment variable.
        /// </summary>
        public async Task<bool> IsTrueAsync(CancellationToken cancellationToken)
        {
            var value = await flag.Resource.GetValueAsync(cancellationToken);

            return bool.TryParse(value, out var result)
                ? result
                : throw new InvalidOperationException(
                    $"{flag.Resource.Name} must be true or false, not '{value}'.");
        }
    }
}
