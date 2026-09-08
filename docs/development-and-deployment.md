# Development and deployment

One AppHost describes Group Split in both places it runs: a developer's machine, and the
Docker host the deploy workflow ships it to. This page is about the seam between the two,
and about the knobs that differ from one deployment to the next.

## Two axes, not one

Aspire keeps two independent questions apart, and so does this repo:

| Question | Values | Read from |
| --- | --- | --- |
| How was the AppHost invoked? | run mode (`aspire start`, `dotnet run`, the test host) or publish mode (`aspire publish`, `aspire do`, `aspire deploy`) | `builder.ExecutionContext.IsRunMode` |
| Which deployment is this for? | `production` today; any name works | `--environment <name>` on the deployment commands |

The AppHost branches on the first. The second only decides which parameter values are
loaded and what the generated env file is called (`.env.production`).

## What lives where

[`AppHost.cs`](../src/GroupSplit.AppHost/AppHost.cs) is the whole model, the way Aspire
expects: resources added with `Add*`, shaped with `With*`, wired with `WithReference` and
`WaitFor`. The top declares what every mode shares: Postgres with the two databases,
Keycloak, the EF migrations, the API and the web app. Then one `IsRunMode` check splits
into two blocks, and each block is short because a resource that has a different shape in
each mode carries that shape as a pair of extension methods next to the code that owns it:

| Resource | Run mode | Publish mode |
| --- | --- | --- |
| Keycloak | [`AsDevelopmentKeycloak`](../src/GroupSplit.AppHost/Extensions/KeycloakDevelopmentExtensions.cs): realm and theme bind-mounted from the checkout, Mailpit as the relay, a data volume | [`AsDeployedKeycloak`](../src/GroupSplit.AppHost/Extensions/KeycloakDeploymentExtensions.cs): served under the public origin at `/idp` behind the web app's forwarder, realm and theme shipped as Compose configs, a Compose healthcheck; plus [`WithSmtp(configuration)`](../src/GroupSplit.AppHost/Extensions/SmtpExtensions.cs) for the relay |
| Postgres | `WithPostgresMcp` on each database (Aspire's own) | [`AsDeployedPostgres`](../src/GroupSplit.AppHost/Extensions/PostgresDeploymentExtensions.cs): the database-creation script the orchestrator would otherwise have run, a Compose healthcheck |
| API and web | [`WithHealthEndpoints`](../src/GroupSplit.AppHost/Extensions/HealthEndpointExtensions.cs): an unpublished management endpoint with liveness and readiness probes | `WithManagementHealthcheck`, in the same file: the readiness probe restated as a Compose healthcheck against that endpoint |
| Compose | not present | `AddDockerComposeEnvironment` with [`WithProtectedDashboard`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs), the image registry, `WithComposeDefaults`, and `WithPrepareOnPush`, which makes `aspire do push` also write the Compose artifacts |

Only run mode adds the MAUI head, the seeder and its dashboard command, Scalar, and Mailpit
itself; only publish mode declares the deployment parameters below. `PublishAs*` calls,
which Aspire ignores in run mode, stay on the shared chain where they read as part of the
resource's definition.

The rule for adding something new: if a deployment would never run it, it goes in the run
block; if it only makes sense once there is no AppHost around to orchestrate, it goes in
the publish block; otherwise it is shared. If a resource needs more than a line or two in
either block, give it an `AsDevelopment*` / `AsDeployed*` pair in its own file under
`Extensions/` rather than growing the block. Files that are shipped into containers, the
realm, the Keycloak theme and the Postgres init script, live under `Assets/`. The theme
restates the app's design tokens rather than importing them, so a style change has to be
made in every view that shows it -- see [design-system.md](design-system.md).

The generic Compose plumbing those methods lean on is in
[`Extensions/DeploymentExtensions.cs`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs):
shipping files as configs, healthchecks, the external network, and the restart and
`depends_on` defaults the publisher leaves to the operator.

## Deployment parameters

Anything that differs between deployments is an Aspire parameter. Parameter names are
kebab-case; the deploy workflow exports every GitHub secret and variable of the
`production` environment as `Parameters__<lower-cased name>`, the spelling Aspire documents
for CI (dashes in a parameter name are written as underscores), so a GitHub entry called
`WEB_HOSTNAME` is what the parameter `web-hostname` resolves to. Adding a parameter to the
AppHost needs only a GitHub entry of the matching name.

| Parameter | GitHub entry | Required | Purpose |
| --- | --- | --- | --- |
| `web-hostname` | variable `WEB_HOSTNAME` | yes | Public origin of the web app, scheme included. Keycloak is served under it at `/idp`, both apps validate tokens against that issuer, and it is the address bank providers are told to deliver webhooks to. Changing it leaves already-linked items pointing at the old one until each is relinked. |
| `dashboard-token` | secret `DASHBOARD_TOKEN` | yes | Browser token for the published Aspire dashboard on port 18888. |
| `cache-password` | secret `CACHE_PASSWORD` | yes | Password for the Redis session cache. Aspire would generate one per publish, which would not match the password the running container was started with. |
| `db-server-password` | secret `DB_SERVER_PASSWORD` | yes | Postgres superuser password, shared by the app and Keycloak databases. |
| `keycloak-password` | secret `KEYCLOAK_PASSWORD` | yes | Keycloak bootstrap admin password. |
| `google-sign-in-enabled` | variable `GOOGLE_SIGN_IN_ENABLED` | no, defaults to `false` | Whether the login page offers Google. |
| `google-client-id`, `google-client-secret` | secrets `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | when Google is enabled | The OAuth client. Missing while enabled fails the publish. |
| `smtp-enabled` | variable `SMTP_ENABLED` | no, defaults to `false` | Whether Keycloak sends mail. See [Email](../README.md#email). |
| `smtp-host`, `smtp-from`, `smtp-user`, `smtp-password` | variables `SMTP_HOST`, `SMTP_FROM`, `SMTP_USER`; secret `SMTP_PASSWORD` | when mail is enabled | The relay. Missing while enabled fails the publish. |
| `smtp-port` | variable `SMTP_PORT` | no, defaults to `587` | Relay port. |
| `plaid-enabled` | variable `PLAID_ENABLED` | no, defaults to `false` | Whether people can link a bank. |
| `plaid-client-id`, `plaid-secret` | variable `PLAID_CLIENT_ID`; secret `PLAID_SECRET` | when bank sync is enabled | The Plaid credentials. The client id is the same in every Plaid environment; the secret is one per environment. Missing while enabled fails the publish. |
| `plaid-env` | variable `PLAID_ENV` | no, defaults to `Sandbox` | Which Plaid environment to talk to: `Sandbox` or `Production`. |
| `bank-key-certificate` | secret `BANK_KEY_CERTIFICATE` | when bank sync is enabled | PKCS#12 certificate, base64 encoded, that the bank access-token key ring is encrypted with. See [The bank access-token key ring](#the-bank-access-token-key-ring). |

The optional ones are declared with
[`AddOptionalParameter`](../src/GroupSplit.AppHost/Extensions/OptionalParameterExtensions.cs),
which supplies the default when configuration has no value: an environment variable from
the workflow wins over it, and a deployment that never mentions mail or Google gets both
switched off rather than a prompt. (Aspire reads an empty value in `appsettings.json` as a
missing parameter, which is why the defaults are in code rather than there.) The two
switches exist so that the AppHost never has to read a parameter's value while it builds
the model; the rules that span several values run as the `validate-smtp` and
`validate-google-sign-in` and `validate-plaid` pipeline steps, after Aspire's `process-parameters` step has
resolved the values and before `build-prereq`, which every image build waits on, so a bad
deployment fails before an image is built.

`KOMODO_*` and `REGISTRY_*` configure the workflow itself and are not exported as
parameters.

Run mode uses the same defaults. The passwords are generated and kept in the AppHost's
user secrets, Mailpit replaces the relay, and Google is the one thing a developer may
want to switch on locally:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:google-sign-in-enabled true
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:google-client-id <id>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:google-client-secret <secret>
```

Bank sync is the other. Plaid's sandbox needs no approval and opens any institution with
`user_good` / `pass_good`:

```bash
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:plaid-client-id <id>
dotnet user-secrets set --project src/GroupSplit.AppHost Parameters:plaid-secret <sandbox secret>
```

Whether bank sync is on is not a switch inside the API. The Plaid connector is registered
when credentials are present, and bank sync is available exactly when a connector answers,
so `plaid-enabled` exists for the deployment's benefit: off, it sends an empty client id,
which the API reads as no Plaid at all. The two ends cannot disagree about it.

## The bank access-token key ring

A Plaid access token is long-lived read access to somebody's bank, so it is the most
sensitive thing the app stores. Tokens are encrypted with ASP.NET Data Protection, whose
key ring is kept in the app database: every instance then reads what any other wrote, and
resetting the database takes the keys and the ciphertext they open together.

On its own that would be close to useless. A database dump would carry the key ring beside
the ciphertext, which is barely different from storing the tokens in the clear. So the ring
is itself encrypted with a certificate the deployment holds as a secret and the database
never sees. Data Protection keeps doing what it is good at, rotating its keys and reading
what older ones wrote, and the thing that unlocks it lives somewhere else.

Generate one once, with a long life because rotating it is not wired up yet:

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 7300 \
  -subj "/CN=GroupSplit Key Ring" -keyout keyring.key -out keyring.crt
openssl pkcs12 -export -inkey keyring.key -in keyring.crt -passout pass: -out keyring.pfx
base64 -w0 keyring.pfx
```

That last line is the value of `BANK_KEY_CERTIFICATE`. Keep the `.pfx` somewhere safe and
delete it from the machine you made it on; losing it loses the stored tokens and nothing
else, and the way back is that everybody links their bank again.

Every check in front of that certificate asks whether it is *there* — the workflow's
`require`, the AppHost's `validate-plaid`, and `KeyRingExtensions.Load`, which goes as far
as valid base64, a readable PKCS#12 and a private key. None of them can tell whether it is
the *same* one the ring was wrapped with, so the API checks that itself at startup: it
unprotects one stored token and refuses to start if it cannot. Deploying a different but
well-formed certificate would otherwise pass every check, mint a fresh key, and leave every
stored token unreadable — and an item whose token is gone can be neither synced, nor
repaired in update mode, nor removed at the provider, because all three need the token.

Locally there is usually no certificate and the ring is stored unwrapped, which is the
ordinary development posture. A deployment is different: the publish refuses to build when
bank sync is on and the certificate is missing, because a deployment without one looks
exactly like a deployment with one until somebody reads the database.

Webhooks need an address the provider can reach: a deployment is served on one, and locally a
dev tunnel supplies one. With no address at all nothing breaks -- none is sent, a sync runs
when the app asks for one, on linking or through **Sync now**, and the nightly sweep catches
whatever a missed webhook would have.

The address is `Banking:PublicOrigin` plus `/webhooks/{provider}`, and a deployment gets it
from `web-hostname`: the same origin everything else is served on, whose `/webhooks` path the
web app forwards to the API. It is configured rather than read off the request on purpose --
the request's host is whatever the caller wrote in the `Host` header, and this address is
handed to a provider as where to deliver somebody's bank activity. A value that is not an
absolute HTTPS origin fails the start, because a provider refuses such an address and the
alternative is finding out from the first person who tries to link a bank.

### Receiving webhooks locally

`WebhookTunnel` in the AppHost's `appsettings.Development.json` puts the web app behind an
[Aspire dev tunnel](https://aspire.dev/integrations/devtools/dev-tunnels/) and sets the API's
`Banking:PublicOrigin` to the address it is given. It is `false` there, and on it appears as
the `webhook-tunnel` resource in the dashboard with the address it was handed:

```bash
dotnet user-secrets --project src/GroupSplit.AppHost set WebhookTunnel true
```

The web app rather than the API, because that is the shape a deployment has: the provider
calls the public origin and the `/webhooks` forwarder carries the call through unchanged, so
the one hop a webhook makes that nothing else does is exercised locally too.

It needs the `devtunnel` CLI and a signed-in account, which is half of why it is off until
somebody asks for it:

```bash
winget install Microsoft.devtunnel
```

```bash
devtunnel user login
```

The other half is that access has to be anonymous -- a provider has no account here and no
token, and what stands in for one is its signature over the bytes it sent, which the API
checks before reading them -- so the whole local web app answers on that address for as long
as the run lasts. Aspire creates the tunnel with the run and tears it down after.

## The sign-in key ring

The web app has a second, unrelated key ring. It protects the sign-in cookie, the
antiforgery token, and the OIDC correlation, nonce and state cookies -- none of which is
stored in the database, so this ring is nothing to do with the one above and the two never
read each other's keys.

It lives in a Docker volume mounted at the container's home directory, and
`DataProtection:KeyRingPath` points the app at it. Both halves come from the AppHost's
`WithKeyRingVolume`, so the mount and the path cannot drift apart.

The volume is the whole point. Without it the ring sits in the container's writable layer
and every deploy takes it: the replacement container cannot decrypt a single cookie its
predecessor issued, which surfaces as `The key {...} was not found in the key ring` against
an antiforgery token, and `Unable to unprotect the message.State` for anybody who was
signing in at that moment. Everyone is silently signed out on each release.

Mounted at the home directory rather than at the keys directory itself, and that is not
cosmetic. Docker seeds a new named volume from the image's contents *and ownership* when
the mount point exists in the image, and creates it root-owned when it does not. These
images run as a non-root user and create the keys directory at runtime rather than baking
it in, so mounting that path directly hands the app a directory it cannot write to. The
home directory does exist in the image and belongs to that user, so the volume inherits it.

Missing `DataProtection:KeyRingPath` outside development is refused at startup rather than
quietly defaulted, for the same reason the certificate above is: the default is what made
this invisible in the first place.

Unlike the bank ring, this one is not encrypted at rest. The threat is different -- it sits
in a volume on the host, and whatever can read the volume can already read the container it
belongs to -- and what it protects is a session, not a bank token.

Deleting the volume is harmless: everybody signs in again.

## Where the access token lives

The browser never holds one, and cannot: it holds a cookie, the cookie carries a lookup key
and nothing else, and every call it makes to the API goes through this app's `/api`
forwarder, which puts the bearer token on the proxied request. That is the whole point of
the arrangement.

`SaveTokens` is off, so the tokens are not in the authentication ticket either. That was the
default and it works, because the ticket is held server-side by the ticket store -- but the
only thing keeping the tokens out of the cookie would then be that store staying configured,
which is a load-bearing detail one line away from being changed. They live in
`ServerSideTokenStore` instead, keyed by the sign-in session, which a cookie cannot carry by
construction rather than by arrangement.

Keeping them current is `Duende.AccessTokenManagement.OpenIdConnect` (Apache-2.0, and not
`Duende.BFF`, which is under Duende's own licence). The OIDC handler saves tokens and has no
opinion about refreshing them, and the three things that go wrong when you refresh them by
hand are all things it already handles:

- **The same refresh token exchanged twice.** Keycloak rotates them one-time-use by default,
  so the second exchange is refused -- and a page load fires several requests at once, every
  one of them holding the same token. One request stays signed in and the rest are signed
  out.
- **A Keycloak restart read as a bad refresh token.** Only `invalid_grant` says the token is
  finished. A 5xx, a timeout, or a wrong client secret says nothing about it, and treating
  them alike signs out every session at once over a fault none of them can see or fix.
- **The exchange being retried.** `AddServiceDefaults` puts the standard resilience handler
  on every HTTP client, and it retries a POST on a timeout or a 5xx. A retry of an exchange
  the realm did in fact process presents a token it has just retired. So that client is
  stripped of it and given the library's own resiliency, which knows the difference.

The store is ours rather than the library's default because of the render mode. Nothing sets
`RenderMode`, so a deployment runs interactive Server, and there a component's call to the
API happens inside a circuit rather than a request: the ambient `HttpContext` is the
long-lived one the circuit was opened over, its ticket was validated once when that
connection was made, and its response started with the WebSocket handshake. Tokens read
through it were frozen at page load and could not be written back, so a page open longer
than the access token's lifetime got a 401 from everything it touched.
`AddBlazorServerAccessTokenManagement` and a store keyed by the principal are what make it
reachable from there.

The one token value that travels through the browser is the `id_token_hint` on sign-out, in
the front-channel redirect the protocol defines for it. It is not a bearer credential for
the API, it goes to the authority that issued it, and without it Keycloak cannot tell which
session is ending and answers with a confirmation page instead of ending it.

## Sessions across a deploy

The sign-in ticket and the tokens beside it both live in Redis, added to the stack as
`cache`. Held in the web container's own memory, as they were, both went with it on every
deploy: the key ring made the cookie decrypt again, and then the session it named was gone
and everybody was challenged afresh. Redis is not replaced when the web image is, so a
deploy now leaves people signed in.

It carries a data volume and snapshots every thirty seconds, so the host rebooting or the
stack being brought fully down costs at most the last few seconds of session writes rather
than every session. Nothing in there is worth backing up -- the worst case is everybody
signing in again.

`CACHE_PASSWORD` is a required secret for the same reason `DB_SERVER_PASSWORD` is: Aspire
generates one per publish otherwise, and the container is already running with the last one.

Two things this does not do. Redis being unreachable stops sign-in and refresh outright,
which is the price of the sessions surviving anything else. And running more than one `web`
replica would still race: the refresh concurrency control is in-process, so two replicas
could exchange one refresh token at once -- and the Data Protection volume above is per-host,
so a second host would need a shared ring as well.

## Building in a sandbox that has no SDK

CI installs .NET with `actions/setup-dotnet`, which fetches it from
`builds.dotnet.microsoft.com`. Some sandboxes -- Claude Code's remote environment among
them -- allow only a narrow set of hosts, and that is not one of them: the download fails
with a 403 from the egress proxy, and so does `dot.net`, `dotnetcli.azureedge.net` and
`aka.ms`. There is no need to give up on building there, because Ubuntu ships a .NET 10
SDK that satisfies `global.json` and `packages.microsoft.com` and `api.nuget.org` are
usually reachable:

```bash
apt-get install -y dotnet-sdk-10.0     # 10.0.1xx, which global.json's rollForward accepts
dotnet tool restore                    # dotnet-ef and nswag, from api.nuget.org
```

Docker is a likelier casualty: the client is usually present with no daemon behind it, so
the AppHost, the integration tests and anything using Testcontainers cannot run. The
three unit test projects in the CI job need none of it. For work that really does need a
database -- running a migration against real PostgreSQL rather than reasoning about its
SQL -- a server from the distribution is enough, and needs no daemon:

```bash
apt-get install -y postgresql
su postgres -c "/usr/lib/postgresql/16/bin/pg_ctl -D /var/lib/postgresql/16/main \
    -o '-c config_file=/etc/postgresql/16/main/postgresql.conf' -l /tmp/pg.log start"
su postgres -c "psql -c \"CREATE ROLE my_user LOGIN PASSWORD 'my_password' SUPERUSER;\""
su postgres -c "psql -c 'CREATE DATABASE my_db OWNER my_user;'"
```

Those names are not arbitrary: they are the connection string
`AppContextPostgreSqlFactory` falls back to when none is configured, so
`dotnet ef database update --project src/GroupSplit.Data.PostgreSQL.Migrations` finds it
with no further setup.

## Preview a deployment locally

The workflow is not the only way to see what a deploy would ship. The same step it runs
can be run from a checkout, minus the pushes:

```bash
Parameters__web_hostname=https://groupsplit.example.com Parameters__dashboard_token=anything Parameters__cache_password=anything aspire do prepare-compose --environment preview --non-interactive
```

The step is named after the Compose environment resource, `compose`, the way the Docker
integration always names it: `prepare-{resource name}`. This builds the images locally,
generates the migration bundle, and writes `docker-compose.yaml`, `.env` and
`.env.preview` to `src/GroupSplit.AppHost/aspire-output`, which is ignored by git. The Compose file is what Komodo receives; the env file is the
values it would be handed alongside.

Two things about that command are easy to trip over:

- **Aspire caches the parameter values it resolves**, per AppHost and per environment, in
  `~/.aspire/deployments/<sha>/<environment>.json`, and loads that file back on the next
  run for the same environment before it looks anywhere else. A value you set once is
  therefore sticky, and an environment variable that disagrees with the cache loses
  silently. Use a fresh `--environment` name for a one-off, or clear the cache for that
  environment with `aspire deploy --environment <name> --clear-cache` (which then deploys;
  deleting the file by hand does the same without deploying).
- **Publish mode does not read user secrets.** The AppHost runs as `Production` there, so
  the Google credentials and passwords you have locally do not carry over. Passwords are
  generated fresh for the environment and cached as above; everything else has to arrive
  as `Parameters__*` -- which includes `cache-password`, since it is declared rather than
  generated by an integration, the same as `dashboard-token`.

`aspire publish --list-steps` shows the publish pipeline without running it; the deploy
pipeline the workflow uses is `aspire do --list-steps` with the same `--environment`.

## How a deploy runs

A push to `main`, in practice a `dev` to `main` merge, triggers
[`deploy.yml`](../.github/workflows/deploy.yml):

1. The required secrets and variables are checked before anything is built, so a missing
   value fails in seconds rather than after the images exist.
2. Every secret and variable is exported as `Parameters__*`, so the AppHost sees them the
   way Aspire's CI guidance describes.
3. `aspire do push --environment production` builds the API, web and migration images,
   pushes them to `registry.sussman.win/group-split` under one timestamp tag, and
   generates the Compose file that references that tag. The generation rides on `push`
   because the AppHost declares the Compose prepare step as required by it, so the two
   share one pipeline run; run separately they would stamp different tags. The workflow
   knows only the documented command, not any step or resource name of the AppHost's.
4. The Compose file and `.env.production` are handed to Komodo, which owns the stack on
   the Docker host, and the stack is redeployed. The job polls the update and fails if the
   deploy did.

Nothing on the runner starts a container. `aspire deploy` would, which is why the
workflow uses `aspire do` instead.

On the host, the web app joins the shared `internal` network as `group-split-web`, where
Caddy routes the public hostname to it. Nothing else publishes a port except the
dashboard, on 18888, behind its token.
