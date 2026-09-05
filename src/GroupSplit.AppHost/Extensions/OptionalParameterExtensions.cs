using Aspire.Hosting.Publishing;

namespace GroupSplit.AppHost.Extensions;

public static class OptionalParameterExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Declares a parameter that resolves to <paramref name="defaultValue"/> unless
        /// configuration supplies one: an environment variable from the deploy workflow, a
        /// user secret, an appsettings entry.
        /// <para>
        /// Aspire has no constant default of its own. <c>AddParameter(name, value)</c> fixes
        /// the value and ignores configuration, and a default written into appsettings.json is
        /// read as missing when it is the empty string, which is exactly what an unused
        /// credential has to be. This is the small <see cref="ParameterDefault"/> that fills
        /// the gap, and it keeps the deploy-time contract in one place: leaving a value out of
        /// the GitHub environment means precisely the default declared here.
        /// </para>
        /// </summary>
        public IResourceBuilder<ParameterResource> AddOptionalParameter(
            string name,
            string defaultValue,
            bool secret = false)
            => builder.AddParameter(name, new ConstantDefault(defaultValue), secret: secret);
    }

    private sealed class ConstantDefault(string value) : ParameterDefault
    {
        public override string GetDefaultValue() => value;

        public override void WriteToManifest(ManifestPublishingContext context)
            => context.Writer.WriteString("value", value);
    }
}
