using System.CommandLine;
using System.Net.Http.Headers;
using GroupSplit.Cli.Api;
using GroupSplit.Cli.Auth;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Output;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Everything a command action is handed. Built once per invocation.
/// <para>
/// The endpoints and the HTTP stack are lazy on purpose: `groupsplit config set server`
/// and `groupsplit completion bash` have to work before any server is configured, and
/// resolving eagerly would make them fail on a fresh machine.
/// </para>
/// </summary>
public sealed class CliContext : IDisposable
{
    private readonly ParseResult _parseResult;
    private readonly Lazy<Endpoints> _endpoints;
    private readonly Lazy<HttpClient> _apiClient;
    private readonly HttpClient _authClient = new();

    public CliContext(ParseResult parseResult, IOutputWriter output)
    {
        _parseResult = parseResult;
        Output = output;
        Config = new ConfigStore();
        Tokens = new TokenStore();
        Discovery = new OidcDiscovery(_authClient);
        DeviceFlow = new DeviceCodeFlow(_authClient);

        _endpoints = new Lazy<Endpoints>(() =>
            new EndpointResolver(Config).Resolve(
                parseResult.GetValue(GlobalOptions.Server),
                parseResult.GetValue(GlobalOptions.Profile)));

        _apiClient = new Lazy<HttpClient>(() =>
        {
            var provider = new TokenProvider(Tokens, DeviceFlow, Discovery, Endpoints);

            var client = new HttpClient(new AuthDelegatingHandler(provider) { InnerHandler = new HttpClientHandler() })
            {
                // The generated client appends relative paths, so the base has to end in a
                // slash or Uri resolution drops the last segment -- "/api" + "groups"
                // would request "/groups".
                BaseAddress = new Uri(Endpoints.Api.ToString().TrimEnd('/') + "/")
            };

            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("groupsplit-cli", CliVersion.Value));

            return client;
        });
    }

    /// <summary>The parsed command line, for commands reading options they declared themselves.</summary>
    public ParseResult ParseResult => _parseResult;

    public IOutputWriter Output { get; }

    public ConfigStore Config { get; }

    public TokenStore Tokens { get; }

    public OidcDiscovery Discovery { get; }

    public DeviceCodeFlow DeviceFlow { get; }

    public Endpoints Endpoints => _endpoints.Value;

    /// <summary>True when the caller passed --yes, so mutations may proceed unprompted.</summary>
    public bool Confirmed => _parseResult.GetValue(GlobalOptions.Yes);

    /// <summary>
    /// The authenticated client the generated API clients are built on. Exposed so a
    /// command can construct whichever of them it needs without this class having to list
    /// every one.
    /// </summary>
    public HttpClient ApiHttpClient => _apiClient.Value;

    public GroupsClient Groups => new(_apiClient.Value);

    public TransactionsClient Transactions => new(_apiClient.Value);

    public UsersClient Users => new(_apiClient.Value);

    public void Dispose()
    {
        _authClient.Dispose();

        if (_apiClient.IsValueCreated)
        {
            _apiClient.Value.Dispose();
        }
    }
}
