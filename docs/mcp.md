# Group Split MCP server

The experimental MCP host exposes group, expense, and account tools over
stateless Streamable HTTP at `/mcp`. Tools that change data require both
`mcp:read` and `mcp:write`. They currently include creating groups, categories,
expenses, merchants, and split rules; archiving groups; and filing, linking,
ignoring, or restoring bank rows. They also add guests, archive categories,
and refresh or sync bank connections. Group names, descriptions, default split rules,
and activity periods can be changed; categories and split rules can be updated or
deleted, and transactions can be deleted. Deletion tools are marked destructive and
should only be called after the user confirms. Other tools inspect groups, expenses, receipts,
accounts, invitations, and join links. Additional tools support transaction editing,
group membership, settlements, bank connections, support reports and staff operations.
Staff operations are subject to the API's staff role checks. CLI-only commands for
local configuration, device login, shell completion and local file paths are not
remote operations; MCP clients handle authentication and their own local files.
Receipt tools can upload and download expense and inbox attachments as base64-encoded
file bytes (JPEG, PNG, WebP, or PDF, up to 10 MiB), and transcribe existing files.
The MCP server cannot read a path on the agent's device; the client must send the file
contents. Client support for passing attachments to tools varies. Saving or dividing a
receipt may change balances, so review and confirm those actions first.
Invitation claim and decline, joining by link, creating group join links, and recording
a settlement with a person are also available to callers with `mcp:write`. Inspect the
invitation or settlement plan and confirm the consequences with the user before acting.
Destructive annotations and tool descriptions request confirmation from compatible
clients; they are not a server-side confirmation protocol. API authorization and
validation still apply to every call.

The server exposes the generated API's user-facing operations, including settlement
requests, support reports, language preferences, receipt-item edits, and filtering of
group and expense summaries. Account deletion and personal-data export are excluded.
Browser-specific theme, identity-provider account management, bank-provider interfaces,
and push-device registration remain in their respective clients. Receipt downloads use
the authenticated API HTTP client directly because the generated download methods discard
the response body. Not every frontend workflow has been tested end to end through MCP.
Invitation and join-link tokens permit access and should be shared deliberately.
Each call exchanges the authenticated user's access token for an API-audience
token and calls the API using the shared generated client. The API resolves the
user and enforces its own role and group authorization.

## Configuration

Set `Mcp__Issuer` to the exact HTTPS Keycloak realm issuer and `Mcp__Resource`
to the externally accessible HTTPS URL ending in `/mcp`. The resource URL is the
required JWT audience and is advertised in protected resource metadata at
`/.well-known/oauth-protected-resource/mcp`. The web host's YARP forwarder sends
both paths to the MCP service, preserving the public host and bearer token.
The MCP service accepts only the configured resource host. Browser Origin
requests are rejected.

Aspire always includes the MCP resource. Set its `mcp-issuer`, `mcp-resource`,
`mcp-exchange-client-id`, and secret `mcp-exchange-client-secret` parameters;
they have no defaults. The resource waits for the API and realm reconciliation.

For production, set GitHub environment variables `MCP_ISSUER`, `MCP_RESOURCE`,
and `MCP_EXCHANGE_CLIENT_ID`, and secret `MCP_EXCHANGE_CLIENT_SECRET` before
deploying. Use the existing web origin for `MCP_RESOURCE`, with `/mcp` appended;
the existing Caddy route to the web host carries both MCP paths. The release
workflow includes and pins the internal MCP image. See [deployment.md](deployment.md)
for the release path.

To obtain the values:

1. Set `MCP_RESOURCE` to the existing `WEB_HOSTNAME` origin plus `/mcp`, for
   example `https://groupsplit.example.com/mcp`.
2. `MCP_ISSUER` is the `issuer` returned by your realm's OpenID Connect
   discovery document. In this deployment it normally has the form
   `https://<web-hostname>/idp/realms/group-split`; verify it against
   `<issuer>/.well-known/openid-configuration`.
3. In the `group-split` Keycloak realm, create a confidential OpenID Connect
   client for the MCP server and enable Standard Token Exchange. Set
   `MCP_EXCHANGE_CLIENT_ID` to its client ID and copy its client secret from
   its Credentials tab to the GitHub environment secret
   `MCP_EXCHANGE_CLIENT_SECRET`. Do not put the secret in source control or chat.
4. In GitHub, open the repository's Settings, Environments, `production`, and
   add the three variables and the one secret above. The existing release
   workflow refuses to start if any is missing.

The values alone are not enough: configure the OAuth client scopes, audiences,
roles and client registration below, then test the exchange before release.

## Keycloak and clients

Keycloak must issue access tokens with the exact `Mcp__Resource` audience and
the `mcp:read` scope. For write tools it must also issue `mcp:write`.
Configure optional `mcp:read` and `mcp:write` client scopes, each with an
audience mapper for that URL, and assign the appropriate scopes to approved OAuth clients. Configure
authorization code with PKCE and exact redirect URIs for each client; do not
enable password or implicit grants. Consult Keycloak's [MCP integration guide](https://www.keycloak.org/securing-apps/mcp-authz-server)
for resource indicators and supported client registration options. The realm
export does not yet provision these client scopes or register assistant clients.
Configure a confidential exchange client with standard token exchange enabled.
The incoming access token must include that client's ID as an audience as well
as the MCP resource URL; configure the exchange client to request the `api`
audience and preserve the user's required role claims. Ensure the client's default
scopes include a subject (`sub`) mapper and the realm-role mapper; otherwise the
API cannot identify the user or enforce staff access. Restrict exchange grants
and enforce the MCP scopes so a read-only caller cannot exercise write tools.

In ChatGPT or Claude, add the public `/mcp` URL as a remote MCP integration,
then complete the Keycloak sign-in and consent flow if the client and Keycloak
registration settings support one another. Client support and registration
requirements vary. Validate the complete flow in your environment before use.
Never paste a bearer token into a chat.

## Status

This is experimental. `python tests/mcp_keycloak_exchange.py` tests Keycloak
26.6 with synthetic accounts in an isolated disposable container, including
identity, API audience, staff role, and invalid-exchange rejection. Authenticated
tool calls have also been tested with locally signed tokens and a simulated API.
The real assistant OAuth flow, production Keycloak configuration, and proxy
deployment have not yet been validated end to end. Do not
expose the host publicly until those checks and authorization tests are complete.
