using Aspire.Hosting.Publishing;

namespace GroupSplit.AppHost.Extensions;

public static class OptionalParameterExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Declares a parameter whose omitted value is represented by the empty string.
        /// Empty is the application's null/absent value for optional deployment settings.
        /// </summary>
        public IResourceBuilder<ParameterResource> AddOptionalParameter(
            string name,
            string defaultValue = "",
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
