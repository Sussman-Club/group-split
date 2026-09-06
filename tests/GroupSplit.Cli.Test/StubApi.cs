using System.Net;
using System.Text;
using System.Text.Json;

namespace GroupSplit.Cli.Test;

/// <summary>
/// A throwaway HTTP server standing in for the API.
/// <para>
/// The generated client is exercised for real against it -- request path, bearer header,
/// status handling, problem-details parsing -- which is the layer a hand-written fake would
/// skip. The bug it exists to catch is exactly the one that got through once: the client
/// streams error bodies, so a problem response arrives on the typed exception rather than as
/// text, and a mapper reading only the text loses the API's error code.
/// </para>
/// </summary>
public sealed class StubApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(string Method, string Path, string? Authorization)> _requests = [];
    private readonly Dictionary<string, (int Status, string Body)> _routes = new(StringComparer.OrdinalIgnoreCase);

    public StubApi()
    {
        // Port 0 is not available to HttpListener, so take one the OS just handed back.
        var port = FreePort();
        BaseAddress = $"http://127.0.0.1:{port}/api";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _ = Task.Run(ServeAsync);
    }

    public string BaseAddress { get; }

    public IReadOnlyList<(string Method, string Path, string? Authorization)> Requests => _requests;

    /// <summary>Answers <paramref name="path"/> with a JSON body serialized from <paramref name="body"/>.</summary>
    public StubApi Returns(string path, object body, int status = 200)
    {
        _routes[path] = (status, JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

        return this;
    }

    /// <summary>Answers with RFC 9457 problem details, the shape every API failure really has.</summary>
    public StubApi Problem(string path, int status, string code, string detail, object? extra = null)
    {
        var problem = new Dictionary<string, object?>
        {
            ["title"] = "Problem",
            ["detail"] = detail,
            ["status"] = status,
            ["code"] = code,
            ["traceId"] = "00-testtrace-0001-01"
        };

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                problem[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(extra);
            }
        }

        _routes[path] = (status, JsonSerializer.Serialize(problem));

        return this;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            _requests.Add((context.Request.HttpMethod, path, context.Request.Headers["Authorization"]));

            var (status, body) = _routes.TryGetValue(path, out var route)
                ? route
                : (404, """{"title":"Not found","status":404,"code":"NOT_FOUND","detail":"No route."}""");

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();

        return port;
    }

    public void Dispose()
    {
        _listener.Close();
    }
}
