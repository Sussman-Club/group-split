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
Staff operations are subject to the API's staff role checks. MCP clients handle authentication and their own local files.
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

`create_expense` given a `merchantId` and no `categoryId`, `splitRuleId` or
`splits` fills them from the user's last expense at that merchant (see
[bank-sync.md](bank-sync.md#suggested-group-and-category)). It fills the group only
when `groupId` is omitted. To record a personal expense regardless, pass the user's
own id as `groupId`.

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
The production OAuth flow has also been validated for an existing deployment. Confirm
the scopes, permissions and connection flow for each client and deployment; the new
plugin and reconciliation workflows still need their own end-to-end validation.

## Guided read-only reviews

Starter prompts: “Summarize November for my household” and “Review my inbox”.
These workflows use `mcp:read`; they do not require or invoke write tools.
The API still checks the signed-in user's group membership and ownership on each
request. Tool annotations guide clients but are not authorization controls.

For a household summary, use `list_groups` to identify the group if needed, then
call `household_summary` with its ID, year, month, and time-zone ID (for example,
`America/New_York`). The API converts the local month to an inclusive start and
exclusive next-month boundary, using the zone's offset at each boundary. The
response includes those UTC instants, category spending, and the caller's gross
paid and share amounts separately for every currency. Settlements are excluded.
Do not add unlike currencies. Missing currencies in exposure mean the caller
neither paid nor had a share in that currency; spending may still show them.
An unavailable time zone or an ambiguous or nonexistent month boundary is refused.
Do not silently guess a group or time zone.

For a custom MCP client or plugin, connect to the authenticated `/mcp` endpoint
as described above and let its tool discovery expose `household_summary` and the
inbox tools. A starter prompt can instruct the assistant to identify the group,
month and time zone, call the summary, and present each currency separately. The
inbox starter can follow the sequence below. These instructions are advisory:
the server and API enforce permissions, and a plugin must not embed user tokens.
Deploy both the API and MCP images from this change before invoking the new tool
contract. Test with a read-only account before enabling it for users.
For inbox review, use `bank_inbox_summary`, `bank_inbox` (page through as needed)
and `list_receipt_inbox`. Review selected items with `get_bank_row` or
`review_receipt_inbox_item`; each returns the source item and expense candidates
without filing, linking, dismissing or changing it. Show dates, amounts, currencies
and candidate details, and treat matches as suggestions, not proof. A duplicate
count is optional and a missing count is not zero. Use the existing suggestion
and preview tools when useful; never assume an empty page covers the whole inbox.
If a user later requests a change, present the intended action and obtain explicit
confirmation before using a write tool. A preview does not reserve data or grant
permission; recheck stale candidates and respect API duplicate refusals. Never set
an override merely to bypass a refusal. The write scope and API authorization
remain mandatory, and clients must not rely on prompt instructions as enforcement.

The [repository plugin](../plugin/README.md) provides MCP-based `groupsplit` and `groupsplit-inbox` skills, plus focused household summary and read-only inbox review skills. Bank-and-receipt reconciliation requires a user decision and multiple separate writes; it is not atomic.
