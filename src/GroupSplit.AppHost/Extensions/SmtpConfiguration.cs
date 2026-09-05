namespace GroupSplit.AppHost.Extensions;

/// <summary>
/// A mail relay, in the shape Keycloak's realm placeholders expect.
/// <para>
/// Every field is a <see cref="ReferenceExpression"/> rather than a string, so that a
/// value sourced from a parameter or from another resource's endpoint stays unresolved
/// until Aspire renders it -- which is what lets a relay be described the same way
/// whether it is a deployment parameter or a container next door.
/// </para>
/// </summary>
internal sealed record SmtpConfiguration(
    ReferenceExpression Enabled,
    ReferenceExpression Host,
    ReferenceExpression Port,
    ReferenceExpression From,
    ReferenceExpression User,
    ReferenceExpression Password);

internal static class SmtpConfigurationExtensions
{
    /// <summary>
    /// Publishes a relay as the variables realms.json interpolates.
    /// <para>
    /// The names here are not free. Publishing does not mount realms.json as a file: the
    /// Compose publisher inlines its text into a Compose config's <c>content</c>, and
    /// Compose interpolates <c>${...}</c> inside that text against its own env file
    /// before Keycloak ever reads it. So every placeholder in realms.json has to spell a
    /// name Compose's env file defines, which means the screaming-snake form of the
    /// parameter it comes from -- <c>smtp-from</c> becomes <c>SMTP_FROM</c>. A name
    /// Compose cannot resolve is silently replaced with a blank string, and Keycloak
    /// refuses to boot on a blank sender address.
    /// </para>
    /// <para>
    /// The same variables are still set on the container, which is what resolves the
    /// placeholders in run mode, where the file really is mounted and Keycloak does its
    /// own substitution. <c>SMTP_ENABLED</c> drives both the realm's <c>auth</c> and its
    /// <c>starttls</c>: a relay this stack talks to authenticates over STARTTLS, and a
    /// disabled one has neither.
    /// </para>
    /// </summary>
    public static IResourceBuilder<T> WithSmtpEnvironment<T>(
        this IResourceBuilder<T> resource,
        SmtpConfiguration smtp)
        where T : IResourceWithEnvironment
    {
        return resource
            .WithEnvironment("SMTP_ENABLED", smtp.Enabled)
            .WithEnvironment("SMTP_HOST", smtp.Host)
            .WithEnvironment("SMTP_PORT", smtp.Port)
            .WithEnvironment("SMTP_FROM", smtp.From)
            .WithEnvironment("SMTP_USER", smtp.User)
            .WithEnvironment("SMTP_PASSWORD", smtp.Password);
    }
}
