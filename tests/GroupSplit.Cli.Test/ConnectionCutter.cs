using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GroupSplit.Cli.Test;

/// <summary>
/// A plain TCP front for a <see cref="StubApi"/> that passes every request on, and answers
/// every one but the request it is told to cut: that one reaches the stub, and then the
/// client's connection is reset instead of answered.
/// <para>
/// <see cref="HttpListenerResponse.Abort"/> cannot stand in for this. On Windows it resets
/// the connection before a status line is written; the managed listener on Linux still
/// flushes a default 200 first, and a DELETE whose body nobody reads then succeeds. A socket
/// closed with a zero linger is a reset on every platform.
/// </para>
/// </summary>
public sealed class ConnectionCutter : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient _upstream = new();
    private readonly Uri _origin;
    private readonly string _method;
    private readonly string _path;

    public ConnectionCutter(StubApi api, string method, string path)
    {
        _origin = new Uri(api.BaseAddress);
        _method = method;
        _path = path;
        _listener.Start();

        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/api";

        _ = Task.Run(AcceptAsync);
    }

    public string BaseAddress { get; }

    public void Dispose()
    {
        _listener.Stop();
        _upstream.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => RelayAsync(client));
        }
    }

    /// <summary>
    /// One request per connection: every answer says <c>Connection: close</c>, so the cut
    /// request always arrives on a fresh connection, where the client does not quietly retry
    /// it as it would one on a reused connection.
    /// </summary>
    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var (method, target, headers, body) = await ReadRequestAsync(stream);

            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(_origin, target));

            if (body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
            }

            foreach (var (name, value) in headers)
            {
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await _upstream.SendAsync(request);
            var answer = await response.Content.ReadAsByteArrayAsync();

            if (method.Equals(_method, StringComparison.OrdinalIgnoreCase)
                && target.Split('?')[0].Equals(_path, StringComparison.OrdinalIgnoreCase))
            {
                client.Client.LingerState = new LingerOption(true, 0);
                return;
            }

            var head = new StringBuilder()
                .Append($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n")
                .Append($"Content-Length: {answer.Length}\r\n")
                .Append("Connection: close\r\n");

            if (response.Content.Headers.ContentType is { } contentType)
            {
                head.Append($"Content-Type: {contentType}\r\n");
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()));
            await stream.WriteAsync(answer);
        }
    }

    private static async Task<(string Method, string Target, List<(string, string)> Headers, byte[] Body)>
        ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];

        while (!EndsWithBlankLine(buffer) && await stream.ReadAsync(one) > 0)
        {
            buffer.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var headers = lines.Skip(1)
            .Select(line => (line[..line.IndexOf(':')].Trim(), line[(line.IndexOf(':') + 1)..].Trim()))
            .ToList();

        var length = headers
            .Where(header => header.Item1.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(header => int.Parse(header.Item2))
            .FirstOrDefault();

        var body = new byte[length];
        await stream.ReadExactlyAsync(body);

        return (requestLine[0], requestLine[1], headers, body);
    }

    private static bool EndsWithBlankLine(List<byte> buffer)
        => buffer.Count >= 4
            && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n';
}
