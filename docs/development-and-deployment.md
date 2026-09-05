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
| Compose | not present | `AddDockerComposeEnvironment` with [`WithProtectedDashboard`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs), the image registry, `WithComposeDefaults`, the `push-and-prepare-compose` pipeline step |

Only run mode adds the MAUI head, the seeder and its dashboard command, Scalar, and Mailpit
itself; only publish mode declares the deployment parameters below. `PublishAs*` calls,
which Aspire ignores in run mode, stay on the shared chain where they read as part of the
resource's definition.

The rule for adding something new: if a deployment would never run it, it goes in the run
block; if it only makes sense once there is no AppHost around to orchestrate, it goes in
the publish block; otherwise it is shared. If a resource needs more than a line or two in
either block, give it an `AsDevelopment*` / `AsDeployed*` pair in its own file under
`Extensions/` rather than growing the block. Files that are shipped into containers, the
realm, the Keycloak theme and the Postgres init script, live under `Assets/`.

The generic Compose plumbing those methods lean on is in
[`Extensions/DeploymentExtensions.cs`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs):
shipping files as configs, healthchecks, the external network, and the restart and
`depends_on` defaults the publisher leaves to the operator.

## Deployment parameters

Anything that differs between deployments is an Aspire parameter. Parameter names are
kebab-case; the deploy workflow exports every GitHub secret and variable of the
`production` environment as `Parameters__<kebab-name>` as well as under its own name, so
a GitHub entry called `WEB_HOSTNAME` is what the parameter `web-hostname` resolves to.

| Parameter | GitHub entry | Required | Purpose |
| --- | --- | --- | --- |
| `web-hostname` | variable `WEB_HOSTNAME` | yes | Public origin of the web app, scheme included. Keycloak is served under it at `/idp`, and both apps validate tokens against that issuer. |
| `dashboard-token` | secret `DASHBOARD_TOKEN` | yes | Browser token for the published Aspire dashboard on port 18888. |
| `db-server-password` | secret `DB_SERVER_PASSWORD` | yes | Postgres superuser password, shared by the app and Keycloak databases. |
| `keycloak-password` | secret `KEYCLOAK_PASSWORD` | yes | Keycloak bootstrap admin password. |
| `google-sign-in-enabled` | variable `GOOGLE_SIGN_IN_ENABLED` | no, defaults to `false` | Whether the login page offers Google. |
| `google-client-id`, `google-client-secret` | secrets `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | when Google is enabled | The OAuth client. Missing while enabled fails the publish. |
| `smtp-enabled` | variable `SMTP_ENABLED` | no, defaults to `false` | Whether Keycloak sends mail. See [Email](../README.md#email). |
| `smtp-host`, `smtp-from`, `smtp-user`, `smtp-password` | variables `SMTP_HOST`, `SMTP_FROM`, `SMTP_USER`; secret `SMTP_PASSWORD` | when mail is enabled | The relay. Missing while enabled fails the publish. |
| `smtp-port` | variable `SMTP_PORT` | no, defaults to `587` | Relay port. |

The optional ones are declared with
[`AddOptionalParameter`](../src/GroupSplit.AppHost/Extensions/OptionalParameterExtensions.cs),
which supplies the default when configuration has no value: an environment variable from
the workflow wins over it, and a deployment that never mentions mail or Google gets both
switched off rather than a prompt. (Aspire reads an empty value in `appsettings.json` as a
missing parameter, which is why the defaults are in code rather than there.) The two
switches exist so that the AppHost never has to read a parameter's value while it builds
the model; the rules that span several values run as the `validate-smtp` and
`validate-google-sign-in` pipeline steps, after Aspire's `process-parameters` step has
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

## Preview a deployment locally

The workflow is not the only way to see what a deploy would ship. The same step it runs
can be run from a checkout, minus the pushes:

```bash
Parameters__web-hostname=https://groupsplit.example.com Parameters__dashboard-token=anything aspire do prepare-compose --environment preview --non-interactive
```

This builds the images locally, generates the migration bundle, and writes
`docker-compose.yaml`, `.env` and `.env.preview` to `src/GroupSplit.AppHost/aspire-output`,
which is ignored by git. The Compose file is what Komodo receives; the env file is the
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
  as `Parameters__*`.

`aspire publish --list-steps` shows the publish pipeline without running it; the deploy
pipeline the workflow uses is `aspire do --list-steps` with the same `--environment`.

## How a deploy runs

A push to `main`, in practice a `dev` to `main` merge, triggers
[`deploy.yml`](../.github/workflows/deploy.yml):

1. The required secrets and variables are checked before anything is built, so a missing
   value fails in seconds rather than after the images exist.
2. Every secret and variable is exported as `Parameters__*`.
3. `aspire do push-and-prepare-compose --environment production` builds the API, web and
   migration images, pushes them to `registry.sussman.win/group-split` under one timestamp
   tag, and generates the Compose file that references that tag. The step is a barrier
   the AppHost defines so that the pushes and the generation share one pipeline run; run
   separately they would stamp different tags.
4. The Compose file and `.env.production` are handed to Komodo, which owns the stack on
   the Docker host, and the stack is redeployed. The job polls the update and fails if the
   deploy did.

Nothing on the runner starts a container. `aspire deploy` would, which is why the
workflow uses `aspire do` instead.

On the host, the web app joins the shared `internal` network as `group-split-web`, where
Caddy routes the public hostname to it. Nothing else publishes a port except the
dashboard, on 18888, behind its token.
