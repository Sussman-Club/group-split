using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test;

public class CircuitStartupTest
{
    [Theory]
    [InlineData(0)]
    [InlineData(8_000)]
    [InlineData(25_000)]
    [InlineData(120_000)]
    public async Task Persisted_state_can_start_a_circuit(int stateSize)
    {
        await using var app = await StartHostAsync();
        var (message, result) = await StartCircuitAsync(app, stateSize);

        Assert.InRange(message.Length, stateSize >= 25_000 ? 32 * 1024 + 1 : 1, 256 * 1024);

        var completion = Assert.IsType<CompletionMessage>(result);
        Assert.Null(completion.Error);
        Assert.NotEmpty(Assert.IsType<string>(completion.Result));
    }

    [Fact]
    public async Task Other_hubs_keep_the_default_message_limit()
    {
        await using var app = await StartHostAsync();

        Assert.Equal(32 * 1024, app.Services.GetRequiredService<IOptions<HubOptions>>()
            .Value.MaximumReceiveMessageSize);
    }

    [Fact]
    public async Task Default_limit_reproduces_the_production_disconnect()
    {
        await using var app = await StartHostAsync(useDefaultLimit: true);
        var (message, result) = await StartCircuitAsync(app, 25_000);

        Assert.True(message.Length > 32 * 1024);
        Assert.Equal("Connection closed with an error.", Assert.IsType<CloseMessage>(result).Error);
    }

    [Fact]
    public async Task Messages_above_the_bounded_limit_are_still_rejected()
    {
        await using var app = await StartHostAsync();
        var (message, result) = await StartCircuitAsync(app, 200_000);

        Assert.True(message.Length > 256 * 1024);
        Assert.Equal("Connection closed with an error.", Assert.IsType<CloseMessage>(result).Error);
    }

    private static async Task<WebApplication> StartHostAsync(bool useDefaultLimit = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

        if (useDefaultLimit)
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        else
        {
            RenderModeConfig.Initialize(RenderModePreference.ServerFirst);
            builder.Services.AddRazorComponents().AddRenderModeComponents();
        }

        var app = builder.Build();
        app.MapBlazorHub();
        await app.StartAsync();
        return app;
    }

    private static async Task<(ReadOnlyMemory<byte> Message, HubMessage Result)> StartCircuitAsync(
        WebApplication app, int stateSize)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await app.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/_blazor"), timeout.Token);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"protocol\":\"blazorpack\",\"version\":1}\u001e").AsMemory(),
            WebSocketMessageType.Text, true, timeout.Token);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(await ReceiveAsync(socket, timeout.Token)));

        var state = string.Empty;
        if (stateSize > 0)
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, byte[]>
            {
                ["regression"] = Encoding.UTF8.GetBytes(new string('x', stateSize))
            });
            var protector = app.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Microsoft.AspNetCore.Components.Server.State");
            state = Convert.ToBase64String(protector.Protect(serialized));
        }

        var protocol = app.Services.GetServices<IHubProtocol>().Single(protocol => protocol.Name == "blazorpack");
        var message = protocol.GetMessageBytes(new InvocationMessage(
            "1", "StartCircuit", ["http://localhost/", "http://localhost/", "[]", state]));
        await socket.SendAsync(message, WebSocketMessageType.Binary, true, timeout.Token);

        var pending = Array.Empty<byte>();
        while (true)
        {
            var received = await ReceiveAsync(socket, timeout.Token);
            var input = new ReadOnlySequence<byte>(pending.Concat(received).ToArray());
            while (protocol.TryParseMessage(ref input, new StartupBinder(), out var result))
            {
                if (result is CompletionMessage { InvocationId: "1" } or CloseMessage)
                    return (message, result);
            }
            pending = input.ToArray();
        }
    }

    private static async Task<byte[]> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return message.ToArray();
    }

    private sealed class StartupBinder : IInvocationBinder
    {
        public Type GetReturnType(string invocationId) => typeof(string);
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [];
        public Type GetStreamItemType(string streamId) => throw new NotSupportedException();
    }
}
