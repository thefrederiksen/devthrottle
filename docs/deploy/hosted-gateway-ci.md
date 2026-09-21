# Hosted Gateway CI deploy

`.github/workflows/deploy-hosted-gateway.yml` redeploys the hosted Gateway container to Azure App
Service (`devthrottle-gw`).

**IT IS MANUAL. MERGING TO `main` DOES NOT DEPLOY.** The workflow's only trigger is
`workflow_dispatch`, so a release is started deliberately - from the Actions tab, or with
`gh workflow run "Deploy hosted Gateway" --ref main`. This is NOT the website's Vercel auto-deploy
and must not be assumed to behave like it: a merged pull request is not a shipped Gateway, and
believing otherwise means waiting for a deploy that is never going to start.

> This paragraph previously said the opposite - that it deployed automatically once CI went green on
> `main`. That was wrong, and it cost a wait on 2026-08-11 while somebody watched for a run that
> could not exist. If you change the trigger, change this sentence in the same commit.

It only ever **redeploys** (rebuild image in ACR, pin the new digest on the production site, which
restarts it, then verify `/healthz`); it never provisions resources or sets app settings.

**Since 2026-09-20 this is an IN-PLACE restart, not a slot swap.** The plan moved to Basic B2 for the
memory ($25 a month against S1's $69), Basic has no deployment slots, and the staging slot was deleted.
So every deploy takes production off the air for as long as the new container takes to start - about a
minute - and the run measures it and fails above 120 seconds. Rolling back is
`rollback-hosted-gateway.yml`, which pins a previous commit's image; there is no swap-back. The
warmed-swap versions of both files are in git history before that date, and `provision-hosted-gateway-slots.yml`
is the (guarded) way back to a slot-capable plan. Full provisioning
stays in `devthrottle_internal` `docs/architecture/step3-azure-deploy/deploy.sh`, and the resource
group, ACR, plan, storage mount and `CC_GATEWAY_DB_CONNECTION` persist across deploys - so **this
workflow stores no secrets**.

## Auth: OIDC, no stored secret

This is a PUBLIC repo, so no long-lived credential is stored. The workflow logs in to Azure with a
GitHub OIDC token (workload identity federation) as the `devthrottle-hosted-gateway` service principal
(appId `d809a2e9-6e0c-47d3-817f-551227f5eda0`), which already holds **Contributor** on the DevThrottle
subscription. Secrets are never exposed to fork pull requests, and the deploy only runs after a
push-triggered CI success on `main`, so untrusted code cannot reach the credential.

## One-time setup

Two of these three are already applied by the CI-wiring change; the **federated credential requires an
Azure AD admin** and must be run by hand.

### 1. Repo variables (non-secret identifiers) - already set

```
AZURE_GW_CLIENT_ID        = d809a2e9-6e0c-47d3-817f-551227f5eda0
AZURE_GW_TENANT_ID        = ab06f736-a43b-4764-aed6-e4f92addd9d8
AZURE_GW_SUBSCRIPTION_ID  = 8641a436-ec6f-471b-a3ed-04c92b76569c
```

### 2. Environment - already created

A `hosted-gateway-production` environment scopes the OIDC subject (and can carry required-reviewer
protection if you want a manual approval gate before each production deploy).

### 3. Federated credential (Azure AD admin - RUN THIS ONCE)

The service principal cannot add this to itself. As an Azure AD admin:

```
az ad app federated-credential create \
  --id d809a2e9-6e0c-47d3-817f-551227f5eda0 \
  --parameters '{
    "name": "github-devthrottle-hosted-gateway-production",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:thefrederiksen/devthrottle:environment:hosted-gateway-production",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

After that, a deploy can be started from the Actions tab (**Deploy hosted Gateway -> Run workflow**)
or with `gh workflow run`. Merging to `main` still does NOT deploy - see the top of this page.

## App settings this workflow does NOT set

The deploy repins an image; it never writes app settings. Any route that needs a credential therefore
needs that setting present on the slots BEFORE the code that reads it arrives, or the route ships and
refuses to serve. Each of these fails CLOSED when unset - a `503` naming the variable, never an open
door - so a missed setting is visible rather than dangerous, but it is still an outage of that route.

| Setting | Guards | Behaviour when unset |
|---|---|---|
| `REPORT_SERVICE_TOKEN` | `GET /gateway/reports/morning`, `/gateway/reports/recipients` | `503`; the daily email stops |
| `ADMIN_SERVICE_TOKEN`  | `POST /gateway/admin/trials/extend` | `503`; the website's admin screen answers "could not confirm" |

`ADMIN_SERVICE_TOKEN` is deliberately a **different secret** from `REPORT_SERVICE_TOKEN`. The report
token guards a read-only report; the admin token can hand a member a year of paid product. A single
shared credential would mean a leak from a reporting cron could give product away, so the two are
separate values checked by separate code paths.

Set it on production with `az webapp config appsettings set`. Writing an app setting RESTARTS the
site, so it costs the same outage a deploy does - about a minute - and it must not be done while a
deploy is running. (There are no slots any more, so the old "set it on the stopped staging slot first"
recipe is gone.) The values live in cc-secrets (entries `admin-service-token` and `gateway-admin-url`) and in the website's Vercel project. **Never in this repository** - it is public.

The website side needs the matching pair in Vercel: `ADMIN_SERVICE_TOKEN` (the same value) and
`GATEWAY_ADMIN_URL` (this Gateway's base URL).

## Verifying

The workflow polls `GET https://devthrottle-gw.azurewebsites.net/healthz` for a sustained `200` that
reports the commit being shipped, then keeps polling for ten more minutes. A `502`/`000` window of about
a minute is the in-place restart and is expected; production never coming back on the new commit fails
the run, and so does an outage over 120 seconds.
