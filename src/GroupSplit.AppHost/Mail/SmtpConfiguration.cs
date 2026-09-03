namespace GroupSplit.AppHost.Mail;

/// <summary>
/// A resolved mail relay, in the shape Keycloak's realm placeholders expect.
/// <para>
/// Every field is a <see cref="ReferenceExpression"/> rather than a string, so that a
/// value sourced from a secret parameter or from another resource's endpoint stays
/// unresolved until Aspire renders it -- which is what lets a relay be described the
/// same way whether it is a deployment secret or a container next door.
/// </para>
/// </summary>
internal sealed record SmtpConfiguration(
    bool Enabled,
    ReferenceExpression Host,
    ReferenceExpression Port,
    ReferenceExpression From,
    ReferenceExpression User,
    ReferenceExpression Password,
    bool Auth,
    bool StartTls)
{
    /// <summary>No relay: mail is simply never sent, the way it behaved before SMTP existed.</summary>
    public static SmtpConfiguration Disabled { get; } = new(
        Enabled: false,
        Host: Blank,
        Port: Blank,
        From: Blank,
        User: Blank,
        Password: Blank,
        Auth: false,
        StartTls: false);

    private static ReferenceExpression Blank => ReferenceExpression.Create($"");
}

internal static class SmtpConfigurationExtensions
{
    /// <summary>
    /// Publishes a relay as the <c>GS_SMTP_*</c> variables that realms.json interpolates.
    /// <para>
    /// Every variable is set even when the relay is disabled: Keycloak's realm import
    /// leaves an unresolved <c>${...}</c> in place verbatim rather than treating it as
    /// empty, so an unset variable would put the literal placeholder text into the
    /// realm's SMTP host.
    /// </para>
    /// </summary>
    public static IResourceBuilder<T> WithSmtpEnvironment<T>(
        this IResourceBuilder<T> resource,
        SmtpConfiguration smtp)
        where T : IResourceWithEnvironment
    {
        return resource
            .WithEnvironment("GS_SMTP_ENABLED", Flag(smtp.Enabled))
            .WithEnvironment("GS_SMTP_HOST", smtp.Host)
            .WithEnvironment("GS_SMTP_PORT", smtp.Port)
            .WithEnvironment("GS_SMTP_FROM", smtp.From)
            .WithEnvironment("GS_SMTP_USER", smtp.User)
            .WithEnvironment("GS_SMTP_PASSWORD", smtp.Password)
            .WithEnvironment("GS_SMTP_AUTH", Flag(smtp.Auth))
            .WithEnvironment("GS_SMTP_STARTTLS", Flag(smtp.StartTls));
    }

    private static string Flag(bool value) => value ? "true" : "false";
}
