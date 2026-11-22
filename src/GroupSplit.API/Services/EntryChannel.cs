using System.Security.Claims;
using GroupSplit.Shared;

namespace GroupSplit.API.Services;

/// <summary>
/// What a transaction written by this request should say about how it was recorded.
/// </summary>
public interface IEntryChannel
{
    /// <summary>
    /// <paramref name="source"/>, or <see cref="TransactionSource.Mcp"/> when the request came
    /// through the MCP server, whatever the path that wrote the transaction would have said.
    /// </summary>
    TransactionSource SourceFor(TransactionSource source);
}

/// <summary>
/// Tells an MCP call by its token, not by anything the caller says about itself.
/// </summary>
/// <remarks>
/// The MCP server exchanges each user's token for one addressed to the API, and Keycloak
/// names the client that asked for the exchange in the new token's <c>azp</c> claim. That is
/// the MCP server's exchange client and nobody else's, so a token that names it came through
/// MCP. A header would have said the same thing for any client willing to send it.
/// <para>
/// Unset, as it is in a deployment without the MCP server, nothing is ever read as MCP.
/// </para>
/// </remarks>
/// <param name="mcpClientId">The MCP server's exchange client, <c>Mcp:ExchangeClientId</c>.</param>
public sealed class EntryChannel(IHttpContextAccessor http, string? mcpClientId) : IEntryChannel
{
    public TransactionSource SourceFor(TransactionSource source) =>
        !string.IsNullOrEmpty(mcpClientId) && http.HttpContext?.User.FindFirstValue("azp") == mcpClientId
            ? TransactionSource.Mcp
            : source;
}
