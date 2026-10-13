# Deployment

Production is one Docker Compose stack on one Docker host. The deploy workflow publishes
the AppHost into that stack, pushes the images to GHCR, and hands the Compose file to
Komodo, which runs it; the values that differ from one deployment to the next come from the
`production` GitHub environment. This page covers that path and operating what it ships.
For how the same AppHost behaves in run and publish mode, see
[architecture.md](architecture.md); for working on your own machine, see
[getting-started.md](getting-started.md).

## How a deploy runs

Merging to `main` deploys production. There is no separate release step: the workflow
claims the next version itself, so the thing that gets deployed always has one immutable
identifier behind it.

### Shipping a change

1. Open a pull request into `dev` and merge it as usual.
2. Open a pull request from `dev` into `main`. It needs one approving review, every
   conversation resolved, and a green CI run.
3. Merge it. That push to `main` is the deploy.

Only `dev` may merge into `main`. GitHub cannot express that as a setting (neither
branch protection nor rulesets can restrict where a pull request comes from), so
[`main-merge-policy.yml`](../.github/workflows/main-merge-policy.yml) says it as a check,
and the check is required on `main`. A pull request from anywhere else fails it and
explains that the change belongs in `dev` first.

### Version numbers

A merge to `main` raises the **patch**: the job reads the newest `app-v*` tag, adds one,
and writes the new tag before it builds anything. A minor or major release has to be
asked for, because nothing in a commit says which it should be. Run the workflow
manually (`Actions` -> `Deploy` -> `Run workflow`) and choose the `bump`. It is the CLI's
release job in shape, [`release-cli.yml`](../.github/workflows/release-cli.yml), including
that a tag already on `HEAD` is reused, so re-running a failed deploy ships the same
version rather than burning a new number.

The version is the image tag and the `Deployment__Version` the AppHost refuses to publish
without, so a deployed stack, its images and its tag all name the same thing. The tag is
claimed before the build, so a build that then fails leaves a version number spent and a
tag on a commit that never shipped.

`app-v*` is the application. `cli-v*` is the command line, which releases on its own and
never touches the deployed stack.

### What has to be in place

| Prerequisite | Why |
| --- | --- |
| The `production` environment allows `main` | The job runs `environment: production`, and the run happens at `refs/heads/main`, so the environment needs a deployment **branch** policy for `main`. |
| Every required secret and variable on that environment | See [Deployment parameters](#deployment-parameters). Checked before anything is built. |
| A Komodo stack named `group-split` (or `KOMODO_STACK`) in `file_contents` mode | The workflow writes the generated Compose file into it. It does not create the stack. |
| The `KOMODO_REGISTRY_ACCOUNT` account in Komodo's registry providers, with a token that has not expired | Komodo logs in to `ghcr.io` with it before pulling, and the images are private. See [The registry account Komodo pulls with](#the-registry-account-komodo-pulls-with). |

### What the workflow does

1. Runs CI and waits for it. `deploy.yml` calls
   [`ci.yml`](../.github/workflows/ci.yml) as a reusable workflow and depends on it, so
   unit and integration tests both have to pass before anything is built or pushed. It is a
   call rather than a `workflow_run` trigger because `workflow_run` reports the run against
   the default branch, and the environment's `main`-only policy would then refuse every
   deployment.
2. Checks the allowlisted secrets and variables, so a missing value fails in seconds rather
   than after the images exist. A feature switch that is off is a notice; one that is on but
   missing its credentials is a failure.
3. Claims the release version and writes its tag.
4. Pins the Aspire CLI to the version the AppHost SDK declares, so the CLI and the
   evaluated model agree.
5. Exports the allowlist as `Parameters__*`, then unsets the GitHub-facing names so nothing
   the build spawns inherits them.
6. `aspire do push --environment production` builds the API, web and migration images,
   pushes them to `ghcr.io/sussman-club/group-split`, and generates the Compose file that
   references them. The generation rides on `push` because the AppHost declares the Compose
   prepare step as required by it, so the two share one pipeline run; run separately they
   would stamp different tags. The workflow knows only the documented command, not any step
   or resource name of the AppHost's.
7. Resolves each pushed tag to its registry digest, so the host pulls exactly what CI built
   rather than a tag that could later be moved.
8. Runs `docker compose config` over the generated file, so a malformed stack fails on the
   runner instead of on the host.
9. Hands the Compose file and `.env.production` to Komodo, which owns the stack on the
   Docker host, and redeploys it. `/execute` is asynchronous, so the job polls the update.
   A deploy that is still running after five minutes is reported as still running, not as
   failed, because the two need different responses. `DeployStack` is not retried: a
   retry after a lost response would start a second deploy of the same stack.
   When the deploy fails, the job prints the failed Compose stages and fetches the log of
   every service Compose names, whether it exited non-zero or its healthcheck never
   passed, before it rolls the stack back, because the rollback recreates the container
   and with it the only copy of that log. The rest is in
   [When a deploy fails](#when-a-deploy-fails).
10. Smoke-tests the public origin and the Keycloak OIDC discovery document, because a
    completed Komodo update proves the stack operation finished, not that the release is
    being served.

The Compose file and a secret-free deployment manifest are kept as run artifacts for a
month, so a past deploy can be inspected. The `.env.production` beside them is not: it holds
every secret in the clear, and artifacts are readable by anyone with read access to the
repository.

Only one deploy runs at a time. Two concurrent runs would push different image tags into the
same Komodo stack and race each other, so a second merge waits rather than cancelling the
first.

### When a deploy fails

What the job does after a failed Komodo update depends on whether the failure was the
registry login, which [`classify-komodo-failure.sh`](../.github/scripts/classify-komodo-failure.sh)
reads from the stages the update records:

| Failed at | Production touched | What the job does |
| --- | --- | --- |
| "Login to Registry" | No. Komodo stops at the login, before it pulls or restarts anything, so production still runs the previous release. | Puts the previous configuration back into Komodo's stack without deploying it, and says to renew the registry token: see [The registry account Komodo pulls with](#the-registry-account-komodo-pulls-with). It does not roll back: a redeploy starts with the same login and would fail the same way. |
| Any other stage | Possibly. Compose recreates the api and web containers before it finds out that a service they wait for has failed, so the site may be down. | Puts the previous configuration back and deploys it again. If that fails too, production is probably down. |

Putting the configuration back even when nothing was redeployed keeps Komodo honest: what
its stack shows, and what a deploy started from Komodo's own UI would run, is what
production runs.

Only the registry login is singled out, because it is the one failure seen for real and
known to leave production as it was. Other stages that probably run before any container
changes, such as pulling the images, are rolled back like everything else: their stage
names have not been seen in a real update, and guessing them wrong would skip a rollback
that was needed. The script's samples are the real updates from run
[37843928725](https://github.com/Sussman-Club/group-split/actions/runs/37843928725), and
CI runs its test (`bash .github/scripts/classify-komodo-failure.test.sh`, which needs
`jq`).

Every failed deploy writes a job summary naming the release, the Komodo update, the stage
it failed at, whether production was touched, and the outcome. A deploy still running
after five minutes, or a request to Komodo that failed outright, is summarised the same
way, with "unknown" when the job cannot tell whether production was touched.

### The registry account Komodo pulls with

The images in `ghcr.io/sussman-club/group-split` are private, so Komodo logs in to GHCR
before it pulls them. It logs in as the account the workflow writes into the stack's
`registry_account`, which is the `KOMODO_REGISTRY_ACCOUNT` variable on the `production`
environment; today that is a person's own GitHub account. Komodo keeps that account's
token under Settings -> Providers, next to the account name.

The token must be a classic personal access token with the `read:packages` scope and
nothing else. GHCR does not accept fine-grained tokens for pulling, and a classic token
cannot be narrowed to one organisation, so `read:packages` lets it read every package its
account can see. That is one more reason to keep it to that scope. The account itself
needs read access to the organisation's `group-split` packages.

When the token expires or is revoked, every deploy fails at "Login to Registry" with
`Error response from daemon: Get "https://ghcr.io/v2/": denied`, and the job summary says
so. Production is unaffected; only deploys stop. To rotate it:

1. Signed in as the registry account, open GitHub's Settings -> Developer settings ->
   Personal access tokens -> Tokens (classic) -> Generate new token (classic). Tick only
   `read:packages`, and choose an expiry (see below).
2. Optionally check it before Komodo does:
   `docker login ghcr.io -u <account> --password-stdin` with the token on standard input
   should print `Login Succeeded`.
3. In Komodo, Settings -> Providers, edit the `ghcr.io` account of that name and replace
   its token. Nothing in the repository or in GitHub's environment changes.
4. Re-run the failed deploy job (the run's "Re-run failed jobs"). The release tag is already
   on `HEAD`, so it deploys the same version again rather than claiming a new one.
5. Delete the old token if it has not simply expired.

Moving to a different account is the same, plus adding that account under Komodo's
providers and changing `KOMODO_REGISTRY_ACCOUNT`; the next deploy writes the new name into
the stack.

**One person's token is a weak dependency.** It expires on that person's schedule, GitHub
mails its expiry warning to that person only, and it stops working if they leave the
organisation. Two ways out, not exclusive:

- **A dedicated machine account** (GitHub's terms allow one free machine account per
  person or organisation), a member of the organisation with read access to the
  `group-split` packages and nothing else. Its token then reads only those packages, its
  expiry reminders can go to a shared inbox, and nobody's personal account is in the deploy
  path. It takes a seat on a paid plan and is one more account to look after. This is the
  recommended option, with a one-year expiry and the renewal date on the team's calendar.
- **A much longer expiry**, up to no expiry at all. It removes the routine failure, but the
  token then lives for as long as nobody revokes it, and a classic token reads every
  package its account can see. If it is chosen, put the expiry date somewhere the team will
  see it before then.

Making the packages public would remove the login altogether, but it publishes the images
of a private repository; that is not done.

### What this does not do

The smoke test does not roll back. It runs after Komodo has deployed, so a release that
fails it is already the one being served; recovering means deploying a known-good version,
and the digest-pinned manifest from an earlier run is what names one. Nothing backs the
database up before the migration container runs either, and migrations are forward-only,
so rolling images back, by hand or by the job after a failed deploy, does not roll the
schema back with them.

Nothing on the runner starts a container. `aspire deploy` would, which is why the
workflow uses `aspire do` instead.

On the host, the web app joins the shared `internal` network as `group-split-web`, where
Caddy routes the public hostname to it. Nothing else publishes a port beyond the host's
loopback address: the dashboard is on `127.0.0.1:18888` and the Keycloak admin console on
`127.0.0.1:8180`, both reached through an SSH tunnel (see
[Reaching the deployed dashboard](#reaching-the-deployed-dashboard) and
[Reaching the deployed admin console](#reaching-the-deployed-admin-console)).

## Deployment parameters

Anything that differs between deployments is an Aspire parameter. Parameter names are
kebab-case; the deploy workflow exports an explicit allowlist of the `production`
environment's secrets and variables as `Parameters__<lower-cased name>`, the spelling
Aspire documents for CI (dashes in a parameter name are written as underscores), so a
GitHub entry called `WEB_HOSTNAME` is what the parameter `web-hostname` resolves to.

The allowlist stops a future, unrelated repository secret from being forwarded into the
AppHost and written into the generated deployment environment.

The workflow reads each entry from the one place the table below names, a variable or a
secret; the same name stored as the other kind is never read. Optional entries fall back to
the defaults declared by the AppHost when they are absent.

The cost is that adding a parameter takes two edits: a GitHub entry of the matching name,
and the same name added to the deploy workflow's allowlist. A GitHub entry the workflow
does not name never reaches the AppHost, which falls back to its default.

| Parameter | GitHub entry | Required | Purpose |
| --- | --- | --- | --- |
| `web-hostname` | variable `WEB_HOSTNAME` | yes | Public origin of the web app, scheme included. Keycloak is served under it at `/idp`, both apps validate tokens against that issuer, and it is the address bank providers are told to deliver webhooks to. Changing it leaves already-linked items pointing at the old one until each is relinked. |
| `dashboard-token` | secret `DASHBOARD_TOKEN` | yes | Browser token for the Aspire dashboard, which is published on the host's loopback port 18888 and reached through an SSH tunnel. See [Reaching the deployed dashboard](#reaching-the-deployed-dashboard). |
| `cache-password` | secret `CACHE_PASSWORD` | yes | Password for the Redis session cache. Aspire would generate one per publish, which would not match the password the running container was started with. |
| `db-server-password` | secret `DB_SERVER_PASSWORD` | yes | Postgres superuser password, shared by the app and Keycloak databases. |
| `storage-accessKey`, `storage-secretKey` | secrets `STORAGE_ACCESSKEY`, `STORAGE_SECRETKEY` | yes | Root credentials of the RustFS receipt store, which the API signs its S3 requests with. Pinned for the same reason as `cache-password`: generated per publish, they recreated the storage container with new root credentials on every deploy, and everything that depends on it waited on its healthcheck each time. Nothing outside the stack ever sees them, so any two random strings will do. |
| `keycloak-password` | secret `KEYCLOAK_PASSWORD` | yes | Keycloak bootstrap admin password. Only used when Keycloak first creates its master realm. |
| `keycloak-admin-user`, `keycloak-admin-password` | variable `KEYCLOAK_ADMIN_USER`; secret `KEYCLOAK_ADMIN_PASSWORD` | yes | An existing master-realm admin, which the `realm-settings` worker signs in as on every deploy. Not the bootstrap admin, which need not exist once somebody has replaced it. Wrong or missing, the worker fails and the API does not start. |
| `keycloak-account-deletion-secret` | secret `KEYCLOAK_ACCOUNT_DELETION_SECRET` | yes | Secret of `account-deletion`, the `group-split` client whose service account holds only `manage-users`, which the API deletes a deleted account's login through. Any long random string (`openssl rand -hex 32`). The `realm-settings` worker creates the client on a realm that lacks it and sets its secret to this value on every deploy, so there is no step to take in Keycloak, and rotating it is changing this secret and deploying. Missing, the publish fails; see [Deleting an account](keycloak.md#deleting-an-account). |
| `google-sign-in-enabled` | variable `GOOGLE_SIGN_IN_ENABLED` | no, defaults to `false` | Whether the login page offers Google. |
| `google-client-id`, `google-client-secret` | secrets `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | when Google is enabled | The OAuth client. Missing while enabled fails the publish. |
| `smtp-enabled` | variable `SMTP_ENABLED` | no, defaults to `false` | Whether Keycloak sends mail, and so whether the sign-in page offers a password reset. See [Email](keycloak.md#email). |
| `smtp-host`, `smtp-from`, `smtp-user`, `smtp-password` | variables `SMTP_HOST`, `SMTP_FROM`, `SMTP_USER`; secret `SMTP_PASSWORD` | when mail is enabled | The relay. Missing while enabled fails the publish. |
| `smtp-port` | variable `SMTP_PORT` | no, defaults to `587` | Relay port. |
| `plaid-enabled` | variable `PLAID_ENABLED` | no, defaults to `false` | Whether people can link a bank. |
| `plaid-client-id`, `plaid-secret` | secrets `PLAID_CLIENT_ID`, `PLAID_SECRET` | when bank sync is enabled | The Plaid credentials. The client id is the same in every Plaid environment; the secret is one per environment. Missing while enabled fails the publish. They reach the API whatever `plaid-enabled` says, and the API links banks whenever it has both, so leave them empty to switch bank sync off. |
| `plaid-env` | variable `PLAID_ENV` | no, defaults to `Sandbox` | Which Plaid environment to talk to: `Sandbox` or `Production`. |
| `plaid-redirect-uri` | variable `PLAID_REDIRECT_URI` | no | Optional. Where an OAuth bank returns to; empty keeps Plaid's popup flow. See [Linking a bank that redirects](#linking-a-bank-that-redirects). |
| `bank-key-certificate` | secret `BANK_KEY_CERTIFICATE` | when bank sync is enabled, or whenever both Plaid credentials are set | PKCS#12 certificate, base64 encoded, that encrypts the bank access-token key ring. Missing then fails the publish, and outside Development the API refuses to start with Plaid configured and no certificate. Empty is only for local development. See [The bank access-token key ring](#the-bank-access-token-key-ring). |
| `receipt-transcription-provider` | secret `RECEIPT_TRANSCRIPTION_PROVIDER` | no, empty preserves legacy fallback | Explicit provider selection: `Veryfi` or `AzureOpenAI`. |
| `veryfi-enabled` | variable `VERYFI_ENABLED` | no, defaults to `false` | Whether receipt attachments can be transcribed through Veryfi. |
| `veryfi-client-id`, `veryfi-username`, `veryfi-api-key` | secrets `VERYFI_CLIENT_ID`, `VERYFI_USERNAME`, `VERYFI_API_KEY` | when Veryfi is enabled | Veryfi API credentials. Missing while enabled fails the publish. |
| `veryfi-log-raw-responses` | variable `VERYFI_LOG_RAW_RESPONSES` | no, defaults to `false` | Logs Veryfi's whole response for an investigation, at Debug, so the API's `GroupSplit.API.Services.ReceiptTranscription.Veryfi` category has to be lowered to Debug too before anything is written. Leave both off outside debugging. |
| `azure-openai-enabled` | variable `AZURE_OPENAI_ENABLED` | no, defaults to `false` | Whether Azure OpenAI receipt transcription is enabled. |
| `azure-openai-endpoint`, `azure-openai-model` | secrets `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_MODEL` | when Azure OpenAI is enabled | The v1 base URL and deployment/model name. The AppHost puts them in the `receipt-transcription` model reference. |
| `azure-openai-api-key` | secret `AZURE_OPENAI_API_KEY` | when Azure OpenAI is enabled | Azure OpenAI API key. |
| `azure-openai-decision-deployment` | variable `AZURE_OPENAI_DECISION_DEPLOYMENT` | no, empty leaves it off | A Microsoft-Decision-1 deployment on the same Azure resource, which suggests a group and category for inbox rows. It uses the endpoint and key above and only runs while `azure-openai-enabled` is `true`. See [Suggested group and category](bank-sync.md#suggested-group-and-category). |

Optional values are declared with [`AddOptionalParameter`](../src/GroupSplit.AppHost/Extensions/OptionalParameterExtensions.cs),
which supplies the default when configuration has no value. The switches decide whether an
integration is active, and validation steps check the cross-field rules after
Aspire's `process-parameters` step has resolved the values and before `build-prereq`, which
every image build waits on.

`KOMODO_*` configure the workflow itself and are not exported as parameters:

| GitHub entry | Required | Purpose |
| --- | --- | --- |
| secrets `KOMODO_URL`, `KOMODO_API_KEY`, `KOMODO_API_SECRET` | yes | Where Komodo's API is and the key the workflow calls it with. |
| variable `KOMODO_REGISTRY_ACCOUNT` | yes | The name of the `ghcr.io` account in Komodo's registry providers (Settings -> Providers) that Komodo logs in with before it pulls the images. Its token is a classic personal access token with `read:packages` on an account that can read the organisation's packages. When that token expires, every deploy fails at Komodo's "Login to Registry" stage; renewing it is a change in Komodo, and moving to somebody else's account is that plus this variable. See [The registry account Komodo pulls with](#the-registry-account-komodo-pulls-with). |
| variable `KOMODO_STACK` | no, defaults to `group-split` | The Komodo stack the Compose file is written into. |

Run mode reads the same parameters from the AppHost's user secrets; setting Google, Plaid
and receipt transcription up locally is in
[getting-started.md](getting-started.md#secrets-you-may-want).

The AppHost maps the provider switch and enablement to API configuration. It models the account
and receipt deployment with Aspire's `AddOpenAI("azure-openai")` and `WithModel("receipt-transcription", ...)`
and gives the API that model reference. Aspire then injects
`ConnectionStrings__receipt-transcription` containing `Endpoint`, `Key`, and `Model`; the API
does not receive those values as separate environment variables. The API's
`AddReceiptTranscription` binds the selector options through the .NET options pattern and resolves
the selected provider from DI when the request scope is created.
A reading is one attempt with a two-minute budget (`ReceiptTranscription:Timeout`), whichever
provider it goes to: Veryfi's HTTP client runs its own resilience pipeline in place of the
standard handler the service defaults add, which would retry the POST and give up after 30 s,
and the OpenAI client's own retries are turned off. Running out of time answers the retryable
`RECEIPT_TRANSCRIPTION_PROVIDER_UNAVAILABLE` 502.
The deployment workflow uses the corresponding `AZURE_OPENAI_*` production variables/secrets shown
in the table above; add them to the `production` environment, and set the switch to `false`
when the feature is not used.

Whether bank sync is on is not a switch inside the API. The Plaid connector is registered
when both credentials are present, and bank sync is available when a connector answers. The
AppHost passes the credentials through whatever `plaid-enabled` says, so the switch only
decides what the publish checks for; to turn bank sync off, leave the credentials empty.

## Preview a deployment locally

The workflow is not the only way to see what a deploy would ship. The same step it runs
can be run from a checkout, minus the pushes:

```bash
Deployment__Version=preview \
Parameters__web_hostname=https://groupsplit.example.com \
Parameters__dashboard_token=anything Parameters__cache_password=anything \
Parameters__keycloak_admin_user=anything Parameters__keycloak_admin_password=anything \
Parameters__keycloak_account_deletion_secret=anything \
aspire do prepare-compose --environment preview --non-interactive
```

The step is named after the Compose environment resource, `compose`, the way the Docker
integration always names it: `prepare-{resource name}`. This builds the images locally,
generates the migration bundle, and writes `docker-compose.yaml`, `.env` and
`.env.preview` to `src/GroupSplit.AppHost/aspire-output`, which is ignored by git. The
Compose file is what Komodo receives; the env file is the values it would be handed
alongside.

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
  as `Parameters__*`. That includes every required parameter the AppHost declares itself,
  which no integration generates: `web-hostname`, `dashboard-token`, `cache-password`, the
  two `keycloak-admin-*` and `keycloak-account-deletion-secret`. Without
  `Deployment__Version` the AppHost refuses to publish at all.

`aspire publish --list-steps` shows the publish pipeline without running it; the deploy
pipeline the workflow uses is `aspire do --list-steps` with the same `--environment`.

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

The check in front of that certificate, `KeyRingExtensions.Load`, goes as far as valid
base64, a readable PKCS#12 and a private key. It cannot tell whether it is the *same* one
the ring was wrapped with, so the API checks that itself at startup: it
unprotects one stored token and refuses to start if it cannot. Deploying a different but
well-formed certificate would otherwise pass every check, mint a fresh key, and leave every
stored token unreadable. An item whose token is gone can be neither synced, nor repaired in
update mode, nor removed at the provider, because all three need the token.

Locally there is usually no certificate and the ring is stored unwrapped, which is the
ordinary development posture and is logged as a warning at startup. Anywhere else it fails
closed, three times over:

- The deploy workflow refuses `PLAID_ENABLED=true` without `BANK_KEY_CERTIFICATE`, before
  anything is built.
- The AppHost's `validate-plaid` step refuses the same publish, for anyone publishing
  without the workflow.
- The API refuses to start outside Development when Plaid is configured and the ring is not
  wrapped (`BankKeyRingVerifier`).

The API links banks whenever it has both Plaid credentials, whatever `plaid-enabled` says,
so all three also refuse credentials left in place behind a switch that is off. A
deployment with no Plaid credentials at all starts without a certificate: nothing it stores
is worth one.

### A ring that was stored unwrapped

For a while this did not fail closed (#328): a deployment could link banks with no
certificate, and the ring was then written unwrapped beside the tokens. Setting the
certificate afterwards does not fix that by itself. `ProtectKeysWithCertificate` only
wraps keys created from then on; Data Protection never rewrites a key it has already
stored, and never deletes one. The old key stays in the table in the clear, stays the
default key until it expires (90 days after it was made), and keeps opening every token it
protected. And every database backup taken while it was there holds the key and the tokens
together, which is live read access to those banks, however the live database is fixed
later.

So "rewrapping" here means making the exposed tokens worthless, not re-encrypting the key.

**1. Find out whether it happened.** On the database server, in the `groupsplit` database:

```sql
-- One row per Data Protection key. Only the bank ring lives in this table; the web
-- app's sign-in ring is in a volume.
SELECT "Id",
       "FriendlyName",
       "Xml" LIKE '%<encryptedSecret%'                     AS wrapped,
       "Xml" LIKE '%the key below is in an unencrypted form%' AS unwrapped,
       substring("Xml" FROM '<creationDate>([^<]+)</creationDate>')     AS created,
       substring("Xml" FROM '<expirationDate>([^<]+)</expirationDate>') AS expires
FROM "DataProtectionKeys"
ORDER BY "Id";

SELECT "Id", "UserId", "InstitutionName", "LinkedAt", "Status"
FROM "BankConnection"
ORDER BY "LinkedAt";
```

Every key `wrapped` and none `unwrapped`: the ring was never stored in the clear, and there
is nothing to do. Any key `unwrapped`: it was, from that key's `created` date on. If there
are no `BankConnection` rows either, nothing the key opens is stored any more (unlinking
removes the item at Plaid, which kills its token); go straight to step 4.

**2. Deploy with a certificate.** Generate one as above, set `BANK_KEY_CERTIFICATE` on the
`production` environment, and deploy. Keys made from now on are wrapped; the old one is
still there and still readable, which the next step needs.

**3. Retire every token the unwrapped key protected.** Each person unlinks each of their
banks: **Unlink** on the linked-banks card, or `groupsplit bank unlink <connection-id>`.
Unlinking hands the token to Plaid's `/item/remove` first, so the token a backup holds stops
working at Plaid. File or skip anything still in the inbox first: unlinking deletes the
connection's imported rows, though expenses already filed from them keep everything but the
link. A connection the API cannot read the token of is unlinked locally only and logged as
an error naming the item; remove that item in the Plaid dashboard by hand.

(Keeping the connections instead means rotating each token in place with Plaid's
`/item/access_token/invalidate`, which returns a new token and kills the old one, and
protecting the new one with a wrapped key. Nothing in the repository does that yet; at this
size, relinking is cheaper than writing it.)

**4. Delete the unwrapped keys, then restart the API.**

```sql
DELETE FROM "DataProtectionKeys"
WHERE "Xml" NOT LIKE '%<encryptedSecret%';
```

Only once `BankConnection` is empty: a token protected with a deleted key can never be
read again, and until the unwrapped key is gone it is still the default, so anything linked
in the meantime is protected with it too. Nobody links a bank between steps 2 and 5. The
API caches the ring, so restart it (redeploy, or restart the `api` container); the first
link after that mints a key wrapped with the certificate. Re-run step 1: no row should
read `unwrapped`.

**5. People link their banks again.** Then re-run step 1 once more: the new key is
`wrapped`.

**6. Treat the old backups as bank credentials.** Any backup of the app database taken
while an unwrapped key and a token sat in it held both. Once step 3 is done, the tokens in
it are dead at Plaid. Until then, and for any connection that could not be removed at
Plaid, keep those backups as tightly held as the Plaid secret itself, and delete the ones
nobody needs.

## The sign-in key ring

The web app has a second, unrelated key ring. It protects the sign-in cookie, the
antiforgery token, the OIDC correlation, nonce and state cookies, and the sign-in tickets and
tokens held in Redis (see [Sessions across a deploy](#sessions-across-a-deploy)). None of
that is stored in the database, and the two rings never read each other's keys.

It lives in a Docker volume mounted at the container's home directory, and
`DataProtection:KeyRingPath` points the app at it. Both halves come from the AppHost's
`WithKeyRingVolume`, so the mount and the path cannot drift apart.

Without the volume the ring sits in the container's writable layer and every deploy takes
it: the replacement container cannot decrypt a single cookie its predecessor issued, which
surfaces as `The key {...} was not found in the key ring` against
an antiforgery token, and `Unable to unprotect the message.State` for anybody who was
signing in at that moment. Everyone is silently signed out on each release.

It is mounted at the home directory, not at the keys directory itself. Docker seeds a new
named volume from the image's contents *and ownership* when the mount point exists in the
image, and creates it root-owned when it does not. These
images run as a non-root user and create the keys directory at runtime rather than baking
it in, so mounting that path directly hands the app a directory it cannot write to. The
home directory does exist in the image and belongs to that user, so the volume inherits it.

Missing `DataProtection:KeyRingPath` outside development is refused at startup rather than
quietly defaulted, for the same reason the certificate above is: the default is what made
this invisible in the first place.

Unlike the bank ring, this one is not encrypted at rest. The threat is different: it sits
in a volume on the host, whatever can read the volume can already read the container it
belongs to, and what it protects is a session, not a bank token.

Deleting the volume is harmless: everybody signs in again.

## Where the access token lives

The browser never holds one, and cannot: it holds a cookie, the cookie carries a lookup key
and nothing else, and every call it makes to the API goes through this app's `/api`
forwarder, which puts the bearer token on the proxied request.

`SaveTokens` is off, so the tokens are not in the authentication ticket either. That was the
default and it works, because the ticket is held server-side by the ticket store. But the
only thing keeping the tokens out of the cookie would then be that store staying configured,
one line away from being changed. They live in `ServerSideTokenStore` instead, keyed by the
sign-in session, which a cookie cannot carry by construction.

Keeping them current is `Duende.AccessTokenManagement.OpenIdConnect` (Apache-2.0, and not
`Duende.BFF`, which is under Duende's own licence). The OIDC handler saves tokens and has no
opinion about refreshing them, and the three things that go wrong when you refresh them by
hand are all things it already handles:

- **The same refresh token exchanged twice.** Keycloak rotates them one-time-use by default,
  so the second exchange is refused, and a page load fires several requests at once, every
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

## How long a session lasts

Two answers, and the sign-in page asks which one applies.

**Unticked: the session ends when the browser does.** The ticket cookie is written without
`Expires`, so the browser drops it on close, and thirty minutes of sliding expiry caps a tab
left open and walked away from. Keycloak's own identity cookie is a session cookie too, so
reopening the browser reaches the login form rather than being waved through by SSO.

**"Keep me signed in": thirty days from that sign-in, absolute.** The cookie is persisted
with that expiry, and the realm's `ssoSessionIdleTimeout` and `ssoSessionMaxLifespan` are
the same thirty days so the refresh token behind it stays exchangeable for exactly as long.
Absolute rather than sliding, and `AllowRefresh` is turned off on the ticket to keep it that
way: the cookie slides on page loads and the Keycloak session slides on refreshes, and two
windows advancing on different events is how one layer comes to expire a session the other
still considers live. The period is stated on the sign-in page, so it has to be a number the
app is willing to say out loud.

The three settings move together or not at all: `RememberedSessionLifetime` in
`AuthenticationExtensions`, and the two realm timeouts in `realms.json`. `RememberMeTest`
reads the realm file and fails if they disagree.

A fourth follows them: `ServerSideTokenStore` holds the tokens beside the ticket rather than
inside it, so the two expire separately and the shorter one decides. It was twelve hours,
which was longer than the cookie when the cookie was thirty minutes. Tokens going first is
the worse way round: the cookie and the ticket are both still good, so the app believes
the person is signed in while every call it makes on their behalf has nothing to make it
with. The store keeps them for the remembered lifetime too.

The checkbox is on the app's own hand-off page rather than Keycloak's, which is why that
page waits for a click instead of redirecting by itself. Keycloak has a "Remember Me" of its
own and keeps the answer: the flag is read into `UserSessionModel` and never written to a
session note, a claim or a token, so an app federated to it cannot find out which was
chosen. It is also dropped on the way to a social provider, so the realm's checkbox would do
nothing at all for a Google sign-in. The realm's `rememberMe` is therefore off, and there is
one checkbox rather than two that disagree.

What this costs: an unticked sign-in still leaves a user session at Keycloak that idles out
on the same thirty days, because the realm has no way to tell the two apart any more. It
grants nothing (both cookies that could reach it are gone with the browser), but it is
rows in the session table for longer than they are worth.

Realm import only creates a realm that does not exist yet. The `realm-settings` worker
reconciles the managed realm-level values from `realms.json` after Keycloak becomes healthy
in both run and publish modes, the flow bindings among them. It does the same for the
`web-app` client's redirect URIs, web origins and post-logout redirect URIs, resolving
`${WEB_HOSTNAME}` from its environment, and for the `cli` client's grants and `api` audience
mapper, creating the `cli` client when the realm has none.
It also reconciles the `google` provider's `trustEmail`, its essential-claim filter on
`email_verified`, and the first broker login flow it names, which is rebuilt whole when it
differs. It creates the `account-deletion` client when the realm lacks it, and holds it to
the secret in `KEYCLOAK_ACCOUNT_DELETION_SECRET` and to its `manage-users` role; see
[Deleting an account](keycloak.md#deleting-an-account).
See [Applying this to a realm that already exists](keycloak.md#applying-this-to-a-realm-that-already-exists).
It leaves SMTP configuration, the rest of each client and provider, other protocol mappers,
the built-in authentication flows, roles, and users alone. Use the local **Reset local
Keycloak realm** command to delete the realm and restart Keycloak when a complete re-import
is needed. Turning `rememberMe` off is not retroactive either: sessions created while it was
on keep their extended lifetime until they expire
([CVE-2025-11429](https://advisories.gitlab.com/pkg/maven/org.keycloak/keycloak-services/CVE-2025-11429/)),
so on a realm that had it on, existing sessions have to be ended rather than waited out.

## Sessions across a deploy

The sign-in ticket and the tokens beside it both live in Redis, added to the stack as
`cache`. Held in the web container's own memory, as they were, both went with it on every
deploy: the key ring made the cookie decrypt again, and then the session it named was gone
and everybody was challenged afresh. Redis is not replaced when the web image is, so a
deploy now leaves people signed in.

It carries a data volume and snapshots every thirty seconds, so the host rebooting or the
stack being brought fully down costs at most the last few seconds of session writes rather
than every session. Nothing in there is worth backing up: the worst case is everybody
signing in again.

Both are encrypted with [the sign-in key ring](#the-sign-in-key-ring) before they are
written, because what is in there is every signed-in person's refresh token, and the volume
is a file on the host. An entry the ring cannot open (one written in plain text before
this was done, or under a key that has since been lost) reads as nothing stored: that
session is signed out and signs in again, and nothing fails.

`CACHE_PASSWORD` is a required secret for the same reason `DB_SERVER_PASSWORD` is: Aspire
generates one per publish otherwise, and the container is already running with the last one.

Two things this does not do. Redis being unreachable stops sign-in and refresh outright,
which is the price of the sessions surviving anything else. And running more than one `web`
replica would still race: the refresh concurrency control is in-process, so two replicas
could exchange one refresh token at once. The Data Protection volume above is per-host too,
so a second host would need a shared ring as well.

## What the public origin exposes of Keycloak

The web app forwards two subtrees under `/idp` to Keycloak, and nothing else:

- `/idp/realms/group-split/**`: the sign-in and registration pages, the OIDC endpoints both apps
  read their metadata and signing keys from, the Google broker callback, and the account
  console;
- `/idp/resources/**`: the static files those pages load.

Everything else under `/idp` answers 404 from the web app and never reaches Keycloak. That
includes the admin console, the admin REST API, the `master` realm and its token endpoint, other
realms, and the welcome page. So does a path carrying an encoded slash or backslash, which could
route as the app's realm here and be read as something else by Keycloak. See
[`MapKeycloakForwarder`](../src/GroupSplit.App/GroupSplit.App.Web/WebAppExtensions.cs).

The seeder, the realm worker and the AppHost tests reach Keycloak inside the network, so none
of them depends on the forwarder. Deployed, Keycloak serves even its internal address below
`/idp`, so the worker is given `Keycloak__RelativePath=/idp` and calls
`http://keycloak:8080/idp/...`. Without it every admin call is a 404, and the API never starts.

### Reaching the deployed admin console

The admin console is reachable only from the host, through an SSH tunnel. Keycloak's HTTP port
is published on the host's loopback address alone (`127.0.0.1:8180`), so neither the internet
nor the LAN can reach it. A port Docker publishes on every interface would walk past the host
firewall.

```bash
ssh -L 8180:127.0.0.1:8180 <deploy-host>
```

With the tunnel open, browse to `http://localhost:8180/idp/admin/` and sign in as the Keycloak
admin. Two settings make the console work at that address rather than at the public one:

- **`KC_HOSTNAME_ADMIN=http://localhost:8180/idp`** moves where the console itself is served.
- **The `master` realm's Frontend URL**, which the realm worker sets on every deploy from
  `Realm__AdminConsoleUrl`, moves the console's sign-in to the same address. Without it the
  console signs in to `master` on the public origin, which now answers 404, and hangs on a blank
  page.

Only `master` gets that frontend URL. The app realm keeps the public origin its users and tokens
need. If 8180 is taken on the host, change `KeycloakDeploymentExtensions.AdminConsolePort`.

For one-off commands without a browser, use the admin CLI inside the container. Pass the
credentials on each call, because each `exec` is a new process and a `config credentials` login
does not carry over. Use the master-realm admin the realm worker signs in as
(`keycloak-admin-user`), with its name and password set in your shell; the bootstrap `admin`
need not exist any more:

```bash
docker compose exec keycloak /opt/keycloak/bin/kcadm.sh get realms/group-split/clients \
  -q clientId=web-app --no-config --server http://localhost:8080/idp \
  --realm master --user "$KEYCLOAK_ADMIN_USER" --password "$KEYCLOAK_ADMIN_PASSWORD"
```

Settings that belong to the repository still go in `realms.json`, where the realm worker applies
them on the next deploy. The admin console and CLI are for one-off inspection and for what the
worker leaves alone.

## Reaching the deployed dashboard

The deployed stack runs the Aspire dashboard as the `compose-dashboard` service, and every service
sends it traces, logs and metrics. It is reachable the same way as the Keycloak admin console:
from the host only, through an SSH tunnel. Its port is published on the host's loopback address
alone (`127.0.0.1:18888`).

```bash
ssh -L 18888:127.0.0.1:18888 <deploy-host>
```

With the tunnel open, browse to `http://localhost:18888` and paste the `DASHBOARD_TOKEN` value when
the dashboard asks for its browser token. Use a different local port on the left of `-L` if 18888
is taken on your machine. The host port is `DeploymentExtensions.DashboardPort`.

The dashboard is published on loopback only for two reasons:

- **It shows what the services record.** Traces and structured logs carry request details and
  error text, and anything a service writes to them by mistake. Treat it as able to show secrets.
- **It speaks plain HTTP.** On a port published on every interface, the browser token and
  everything the dashboard shows crossed the LAN unencrypted. Docker publishes ports past the
  host firewall, so ufw would not have stopped it either. Through the tunnel, SSH provides the
  encryption and the authentication. The token is still required, as a second lock for anyone
  who already has a shell on the host.

[`WithProtectedDashboard`](../src/GroupSplit.AppHost/Extensions/DeploymentExtensions.cs) writes
the mapping, and `PublishedDashboardTest` reads it back from a generated Compose file.

### What telemetry leaves out

Some routes are addressed by a secret rather than an id. Whoever has the URL has the access it
grants:

- the invitation claim link: `/invitations/claims/{token}` on the API, `/claim/{token}` on the web;
- the join link: `/invitations/links/{token}` on the API, `/join/{token}` on the web.

Instrumentation records paths as they were requested. Without redaction, every one of these
tokens would reach the dashboard in `url.path` on server spans, in `url.full` on `HttpClient`
spans (including the web app's forwarders), and in the request URIs that ASP.NET Core and
`HttpClient` log. [`TokenRedaction`](../src/GroupSplit.ServiceDefaults/TokenRedaction.cs) in
the service defaults replaces the token segment with `Redacted` before anything is exported, so
`/join/abc` is recorded as `/join/Redacted`. `http.route` keeps the template, `{token}` included.
OpenTelemetry already redacts query-string values the same way.

A new route that carries a secret in its path has to be added to the pattern in
`TokenRedaction`, with a case in `TokenRedactionTest`.

## Sign-in and admin events

The `group-split` realm records user events and admin events, so there is something to read
after the fact: who signed in or failed to, who linked or unlinked a Google account, who asked
for or completed a password reset, who changed their email or profile or deleted their account,
and which admin changed a client, a flow or a user. `realms.json` sets this and the realm worker
applies it on every start, so a realm imported before it said so records them too:

- **User events** are on and kept for 90 days (`eventsExpiration`, in seconds). They cover the
  types in `enabledEventTypes`: signing in and out, registering, the Google first sign-in, link
  and unlink, password and credential changes, password reset, email verification and change,
  profile change, account deletion, and the CLI's device sign-in, each with its `_ERROR` twin.
- **Admin events** are on, without their details (`adminEventsDetailsEnabled` is `false`). The
  details are the representation that was sent, and that can carry a secret: a client or
  identity provider secret, or a user's initial password. The event still says who did what to
  which resource, and when.

Where to read them:

- **The admin console**: *Events* in the `group-split` realm, with a tab for user events and
  one for admin events, filterable by type, user, client and date. Deployed, the console is
  reached through the SSH tunnel described above.
- **The container log**, through the realm's `jboss-logging` listener. Failures are logged at
  `WARN` by default; the AppHost sets `KC_SPI_EVENTS_LISTENER__JBOSS_LOGGING__SUCCESS_LEVEL=info`
  so that successful ones are logged too, as lines starting `type="LOGIN"` and so on. Locally
  that is the `keycloak` resource's console log in the Aspire dashboard; deployed, it is
  `docker compose logs keycloak` on the host, which keeps them only as long as Docker keeps the
  log. Keycloak's OTLP export carries its traces, not its log, so these lines do not reach the
  deployed dashboard; that would take Keycloak's preview `opentelemetry-logs` feature.

The event store is Keycloak's own database, so the 90 days survive restarts and deploys.

## Security headers and the cross-site check

Every response the web app writes itself carries a content security policy, `X-Frame-Options:
DENY`, `Referrer-Policy: strict-origin-when-cross-origin` and `nosniff`; outside development,
HSTS for a year with subdomains. The policy is in `SecurityHeadersExtensions`, one directive per
thing a page loads: `'wasm-unsafe-eval'` for the WebAssembly runtime, inline styles for
MudBlazor, Google Fonts, Plaid Link, Plaid's logo hosts, and this origin's own WebSocket for the
server-rendered mode. The import map is the page's one inline script and is let in by a nonce.
Adding a script, a frame or a font from somewhere new means adding it there too; otherwise the
browser refuses it with only a line in the console to say so.

Responses under `/idp` are Keycloak's, and keep the headers Keycloak sends.

Writes through the cookie-authenticated `/api` route must show they came from the app's own
pages: `Sec-Fetch-Site: same-origin`, or the `X-GroupSplit-Request` header the WebAssembly
client adds. Anything else is answered 403 `CROSS_SITE_REQUEST_REFUSED` and never forwarded.
Signing out is a form `POST` to `/auth/logout` carrying the page's antiforgery token.

The API limits, per person and per hour, the requests that cost money or invite guessing:
receipt transcription (30), bank calls (30) and invitations (60). A refusal is 429
`RATE_LIMITED`; the routes each limit covers, and the `RateLimits` configuration that changes
them, are in [errors.md](errors.md#too-many-requests-429). The limits are keyed on the token's
subject and never on the address, because the deployment trusts `X-Forwarded-For` from
whoever sends it. They are counted in the API's memory, so a deploy resets them, and a second
API instance would keep its own count.

## Linking a bank that redirects

Most institutions run their sign-in inside a popup: Plaid Link opens, the person signs in,
and the page that opened it is still there to be answered. Some OAuth institutions take the
whole browser to the bank's own site instead, and then the page that started the link is
gone and the token it was holding went with it.

Nothing needs configuring for the popup flow, which is what every deployment runs today.
For the redirect flow, two addresses have to be the same one:

1. Register `https://your-origin/bank/oauth` as a redirect URI in the Plaid dashboard.
2. Set `plaid-redirect-uri` to exactly that.

`/bank/oauth` is served by the web app and is not configurable. It is the page that picks
the interrupted session back up, using the token the tab wrote down before Link opened. A
value pointing anywhere else is refused when the API starts, because the alternative is
somebody signing in at their bank and landing on a page that cannot finish what they
started, with the bank believing it is connected.

"Anywhere else" is measured against `Banking:PublicOrigin` where a deployment sets it: the
redirect has to be on that origin, and end in `/bank/oauth`. Without it (locally, where no
public origin is given) only the path can be checked, so a value on the wrong host is
caught by Plaid refusing the link token instead.

The two ends cannot check each other at runtime. Plaid matches the registered address
exactly and refuses to mint a link token against one it does not recognise, so a mismatch
in the dashboard is every OAuth bank failing to link.

> Mobile is not covered by this. Plaid's OAuth on a native app goes through app-to-app
> redirects and universal links, which is a different mechanism from the web return page
> above.
