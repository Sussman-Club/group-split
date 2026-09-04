using GroupSplit.AppHost.Mail;
using Microsoft.Extensions.Configuration;

namespace GroupSplit.AppHost.Keycloak;

public static class SmtpExtensions
{
    // Deploy-time values arrive as Aspire parameters. The deploy workflow turns every
    // GitHub secret and variable into Parameters__<kebab-name>, so SMTP_HOST, SMTP_FROM,
    // SMTP_USER and SMTP_PASSWORD there land on the names below with no extra wiring.
    // These names also decide what realms.json can interpolate -- see WithSmtpEnvironment.
    private const string HostParameterName = "smtp-host";

    private const string PortParameterName = "smtp-port";

    private const string FromParameterName = "smtp-from";

    private const string UserParameterName = "smtp-user";

    private const string PasswordParameterName = "smtp-password";

    private const string AuthParameterName = "smtp-auth";

    private const string StartTlsParameterName = "smtp-starttls";

    // Not an SMTP_ name: it is a realm policy rather than part of the relay.
    private const string VerifyEmailParameterName = "verify-email";

    // Submission with STARTTLS. Port 25 is blocked outbound by most hosts, and 465 wants
    // implicit TLS, which is the realm's `ssl` flag rather than its `starttls` one.
    private const string DefaultPort = "587";

    // Keycloak validates the sender address while importing the realm and refuses to
    // start on one it cannot parse -- an empty string included. So the unconfigured case
    // still needs a syntactically valid address, and .invalid is reserved by RFC 2606
    // precisely so that it can never resolve. With no host to connect to, nothing sends.
    private const string UnroutableSender = "no-reply@group-split.invalid";

    extension(IResourceBuilder<KeycloakResource> keycloak)
    {
        /// <summary>
        /// Points the realm's SMTP server at a relay described by deployment parameters.
        /// <para>
        /// The values reach the realm's <c>smtpServer</c> block through the
        /// <c>${...}</c> placeholders in realms.json, so no credential is committed.
        /// Provider-agnostic on purpose -- host, port, user and sender are all
        /// parameters -- so changing relay is a secret change rather than a code one.
        /// </para>
        /// <para>
        /// With no relay configured this degrades to SMTP disabled rather than failing
        /// the deploy, and registration keeps working rather than trapping every new user
        /// behind a verification mail that could never be delivered. A relay that is only
        /// partly configured is a different matter, and fails the publish.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp(IConfiguration configuration)
        {
            var host = configuration[$"Parameters:{HostParameterName}"];
            var from = configuration[$"Parameters:{FromParameterName}"];
            var user = configuration[$"Parameters:{UserParameterName}"];
            var password = configuration[$"Parameters:{PasswordParameterName}"];
            var port = configuration[$"Parameters:{PortParameterName}"];

            var relay = new Dictionary<string, string?>
            {
                [HostParameterName] = host,
                [FromParameterName] = from,
                [UserParameterName] = user,
                [PasswordParameterName] = password
            };

            var absent = relay
                .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => entry.Key)
                .ToArray();

            var configured = absent.Length == 0;

            // The four stand or fall together: a realm that advertises password reset over a
            // relay that rejects every send is worse than one that never offered it, so a
            // partly described relay fails the publish instead of degrading to "no relay".
            //
            // The sender alone does not count as describing one. Aspire's deployment state
            // cache feeds every resolved parameter back in on the next publish for the same
            // environment, the unroutable placeholder this method supplies included.
            var described = !string.IsNullOrWhiteSpace(host)
                            || !string.IsNullOrWhiteSpace(user)
                            || !string.IsNullOrWhiteSpace(password);

            if (described && !configured)
            {
                throw new InvalidOperationException(
                    "Incomplete SMTP configuration: set every one of "
                    + string.Join(", ", relay.Keys.Select(name => $"Parameters:{name}"))
                    + " or none of them. Missing: " + string.Join(", ", absent) + ".");
            }

            var builder = keycloak.ApplicationBuilder;

            // Declared unconditionally, and with a value for every one of them. A
            // parameter that is absent leaves its name undefined in the published env
            // file, and an undefined name is what Compose turns into the blank string
            // that stops Keycloak from starting.
            var smtp = new SmtpConfiguration(
                Enabled: configured,
                Host: Parameter(builder, HostParameterName, configured ? host! : string.Empty),
                Port: Parameter(builder, PortParameterName,
                    string.IsNullOrWhiteSpace(port) ? DefaultPort : port),
                From: Parameter(builder, FromParameterName, configured ? from! : UnroutableSender),
                User: Parameter(builder, UserParameterName, configured ? user! : string.Empty),
                Password: Parameter(builder, PasswordParameterName,
                    configured ? password! : string.Empty, secret: true),
                Auth: Parameter(builder, AuthParameterName, Flag(configured)),
                StartTls: Parameter(builder, StartTlsParameterName, Flag(configured)));

            // Having a relay and trusting it are separate decisions. A sender domain part
            // way through verification at the provider has every send rejected, and
            // turning verification on then would strand existing users at their next
            // login behind a mail that cannot arrive. So it is opt-in, once mail flows.
            var requested = configuration[$"Parameters:{VerifyEmailParameterName}"];

            var verifyEmail = smtp.Enabled
                              && bool.TryParse(requested, out var parsed)
                              && parsed;

            return keycloak
                .WithSmtpEnvironment(smtp)
                .WithEnvironment("VERIFY_EMAIL",
                    Parameter(builder, VerifyEmailParameterName, Flag(verifyEmail)));
        }

        /// <summary>
        /// Points the realm's SMTP server at Mailpit.
        /// <para>
        /// Mailpit accepts everything and delivers nothing, so local development needs
        /// neither a relay account nor a secret, and a reset or verification mail can be
        /// read in Mailpit's inbox rather than taken on trust. Plain literals rather than
        /// parameters, because run mode mounts realms.json and leaves the substitution to
        /// Keycloak, reading the container's own environment.
        /// </para>
        /// </summary>
        public IResourceBuilder<KeycloakResource> WithSmtp(
            IResourceBuilder<MailPitContainerResource> mailpit)
        {
            var endpoint = mailpit.Resource.PrimaryEndpoint;

            var smtp = new SmtpConfiguration(
                Enabled: true,
                Host: ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Host)}"),
                Port: ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Port)}"),
                // Mailpit accepts any sender, but Keycloak still puts this in the From
                // header. `.localhost` is reserved, so a stray real send cannot resolve.
                From: Literal("no-reply@group-split.localhost"),
                User: Literal(string.Empty),
                Password: Literal(string.Empty),
                Auth: Literal(Flag(false)),
                StartTls: Literal(Flag(false)));

            return keycloak
                .WithSmtpEnvironment(smtp)
                // On locally, where the verification mail is one click away in the inbox.
                .WithEnvironment("VERIFY_EMAIL", Flag(true))
                .WaitFor(mailpit);
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static ReferenceExpression Literal(string value) =>
        ReferenceExpression.Create($"{value}");

    private static ReferenceExpression Parameter(
        IDistributedApplicationBuilder builder,
        string name,
        string value,
        bool secret = false)
    {
        var parameter = builder.AddParameter(name, () => value, secret: secret);

        return ReferenceExpression.Create($"{parameter.Resource}");
    }
}
