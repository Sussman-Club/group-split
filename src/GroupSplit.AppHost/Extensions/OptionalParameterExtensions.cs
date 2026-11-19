using Aspire.Hosting.Publishing;

namespace GroupSplit.AppHost.Extensions;

public static class OptionalParameterExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Declares an optional parameter. Aspire 17 represents an omitted or empty value
        /// as null; consumers treat null and the empty string as absent.
        /// </summary>
        public IResourceBuilder<ParameterResource> AddOptionalParameter(
            string name,
            string defaultValue = "",
            bool secret = false)
            => builder.AddParameter(name, new ConstantDefault(defaultValue), secret: secret).AsOptional();
    }

    private sealed class ConstantDefault(string value) : ParameterDefault
    {
        public override string GetDefaultValue() => value;

        public override void WriteToManifest(ManifestPublishingContext context)
            => context.Writer.WriteString("value", value);
    }
}
