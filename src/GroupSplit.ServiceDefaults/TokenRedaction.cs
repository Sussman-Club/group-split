using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace GroupSplit.ServiceDefaults;

/// <summary>
/// Keeps the tokens that travel in URL paths out of telemetry.
/// <para>
/// A few routes are addressed by a secret rather than an id: whoever holds the path holds the
/// right it grants. An invitation's claim link (<c>/invitations/claims/{token}</c>, the web's
/// <c>/claim/{token}</c>) and a group's join link (<c>/invitations/links/{token}</c>, the
/// web's <c>/join/{token}</c>) are both bearer credentials. Instrumentation records the path
/// as it was requested -- <c>url.path</c> on a server span, <c>url.full</c> on a client span,
/// the URI in <c>HttpClient</c>'s own request log -- so without this every one of them would
/// be exported over OTLP and sit in the dashboard for anyone who can open it.
/// </para>
/// <para>
/// The segment is replaced with <see cref="Placeholder"/>, the word OpenTelemetry's own
/// instrumentation already writes over query string values, so a redacted path reads the same
/// whichever part of it was hidden. The route template (<c>http.route</c>) is left alone: it
/// is the declared pattern, <c>{token}</c> and all, and never the value.
/// </para>
/// <para>
/// Matched anywhere in the string rather than at the start of a path, because the same token
/// appears behind different prefixes depending on who is asking: bare on the API,
/// <c>/api/...</c> and <c>/native/api/...</c> on the web app's forwarders, and after a scheme
/// and host in a full URL. Erring towards hiding something that was not a token is the
/// cheaper mistake here.
/// </para>
/// </summary>
public static partial class TokenRedaction
{
    /// <summary>What a token segment is replaced with.</summary>
    public const string Placeholder = "Redacted";

    /// <summary>
    /// The span attributes that carry a requested path or URL. <c>http.target</c> and
    /// <c>http.url</c> are their names before the semantic conventions settled, still written
    /// by some libraries.
    /// </summary>
    private static readonly string[] UrlTags = ["url.path", "url.full", "url.original", "http.target", "http.url"];

    /// <summary>
    /// The segment after one of the token-addressed route prefixes. Case-insensitive because
    /// routing is, so <c>/Join/abc</c> reaches the same page with the same token.
    /// </summary>
    [GeneratedRegex(
        @"(?<=/(?:invitations/(?:claims|links)|claim|join)/)[^/?#\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenSegment();

    /// <summary>
    /// <paramref name="value"/> with every token segment replaced by <see cref="Placeholder"/>;
    /// the same instance when there was none.
    /// </summary>
    public static string Redact(string value) => TokenSegment().Replace(value, Placeholder);

    /// <summary>Redacts the URL attributes and the name of a finished span.</summary>
    public static void Redact(Activity activity)
    {
        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is string value)
            {
                var redacted = Redact(value);

                if (!ReferenceEquals(redacted, value))
                {
                    activity.SetTag(tag, redacted);
                }
            }
        }

        // Named after the route template when there is one, which holds no token; but a
        // request no route matched is named however its instrumentation saw fit.
        activity.DisplayName = Redact(activity.DisplayName);
    }

    /// <summary>
    /// Redacts a log record's message and every string or URI among its attributes.
    /// <para>
    /// <c>HttpClient</c> logs the URI of every request it starts at Information, the level
    /// the services log at, and its parameters are where the URI lives as data. Attributes
    /// are only replaced when something in them changed, so the common record pays for one
    /// scan and no allocation.
    /// </para>
    /// </summary>
    public static void Redact(LogRecord record)
    {
        if (record.FormattedMessage is { } message)
        {
            record.FormattedMessage = Redact(message);
        }

        if (record.Body is { } body)
        {
            record.Body = Redact(body);
        }

        if (record.Attributes is not { Count: > 0 } attributes)
        {
            return;
        }

        List<KeyValuePair<string, object?>>? rewritten = null;

        for (var i = 0; i < attributes.Count; i++)
        {
            var (key, value) = attributes[i];

            var text = value switch
            {
                string s => s,
                Uri uri => uri.OriginalString,
                _ => null
            };

            if (text is null)
            {
                continue;
            }

            var redacted = Redact(text);

            if (ReferenceEquals(redacted, text))
            {
                continue;
            }

            rewritten ??= [.. attributes];
            rewritten[i] = new KeyValuePair<string, object?>(key, redacted);
        }

        if (rewritten is not null)
        {
            record.Attributes = rewritten;
        }
    }

    /// <summary>
    /// Applies <see cref="Redact(Activity)"/> as each span ends. Has to be registered ahead of
    /// the exporter, which reads the span on the same call.
    /// </summary>
    public sealed class SpanProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => Redact(data);
    }

    /// <summary>
    /// Applies <see cref="Redact(LogRecord)"/> to each record. Has to be registered ahead of
    /// the exporter, which reads the record on the same call.
    /// </summary>
    public sealed class LogProcessor : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data) => Redact(data);
    }
}
