---
name: deploy-hosted-gateway
description: The ONLY way to deploy the hosted Gateway, the Cockpit and the mobile app - all three ship in one image, so this one skill covers all three. Starts the GitHub deploy, watches it, and reports the measured outage. This is the CLOUD Gateway, not the downloadable desktop app. Triggers on "release the gateway", "deploy the gateway", "push the gateway live", "update the hosted gateway", "redeploy the gateway", "deploy the cockpit", "update the cockpit", "ship the cockpit", "deploy mobile", "deploy the mobile app", "deploy to production".
---

# Deploy the hosted Gateway

Updates the live cloud Gateway that DevThrottle clients talk to. It rebuilds the
Gateway container and pushes it live to Azure, then confirms the service is
healthy again.

**It covers the Cockpit and the mobile app too.** They are built INTO the Gateway
container image (`wwwroot/c` and `wwwroot/mobile` - see the repo-root `Dockerfile`).
There is no separate Cockpit deploy and no separate mobile deploy. If someone asks
you to "deploy the Cockpit", this is the skill.

This is NOT the desktop-app release. Cutting a version and building the
downloadable `cc-director.exe` is a different job - use the `release-manager`
skill for that. This skill only redeploys the cloud service; it never touches
version numbers, release notes, tags, or the mailing list.

## What you must know first

- **This workflow is the ONLY way to update the live Gateway.** Do not pin an
  image, restart the site, or change app settings by hand with `az`, and do not do
  it in the Azure Portal. Those paths skip the outage measurement and the refusals
  below, and a hand-driven change is how you get an unmeasured outage on live
  sessions. If this workflow cannot do what is needed, that is a gap to fix in the
  workflow, not to work around. Rolling back is also a workflow
  (`rollback-hosted-gateway.yml`) - use it rather than pinning an old image by hand.
- **A person authorizes the go-live.** Starting this deploy pushes new code to
  the live service. Get an explicit go from the human before you start it. Do not
  start it on your own initiative.
- **It ships main, and never a commit whose checks have FAILED.** The run refuses, in
  seconds and before it touches anything, if it was started against any ref other than
  `refs/heads/main`, or if a check on the commit being shipped has COMPLETED and did
  not pass. Both refusals sit ahead of the Azure login and the build, so a refused
  deploy costs seconds and touches nothing.
- **A check that has not finished is NOT a refusal, and this deploy does not wait for
  one.** The run lists the pending checks and carries straight on, because a pending
  check is an absence of information rather than evidence of a fault. A commit with no
  checks at all is not refused either. Local verification is the gate; continuous
  integration is the backstop that reports afterwards. CLAUDE.md 5a says that about
  MERGES; the workflow reaches the same conclusion on its own account for deploys.

  This bullet said the opposite for twenty-five days, and the cost was real. The
  wait-for-CI wording was written on 2 August 2026 at 17:06 (#2389); the gate stopped
  behaving that way at 22:12 the SAME DAY (#2406, "stop the gate waiting for CI -
  refuse a FAILED check, never a pending one"). The step is even named "Refuse to
  deploy a commit whose checks have FAILED". Anyone who trusted this page instead of
  reading the workflow parked a deploy behind an hour of .NET CI that nothing asked
  for. If this page and
  `.github/workflows/deploy-hosted-gateway.yml` ever disagree again, the workflow is
  the truth and this page is the defect.
- **It does not set up any infrastructure.** The Azure resource group, container
  registry, database connection, and storage are already provisioned and persist
  across deploys. This deploy only: rebuild the image, point the live service at
  the new image, restart, verify health. It needs no stored secrets - it signs in
  to Azure through a trust that is already configured.
- **A deploy takes production off the air for about a minute, and that is expected.**
  Since 20 September 2026 the plan is Basic B2, which has no deployment slots, so the
  deploy is an in-place restart: the old container stops, the new one starts, and the
  gap is the new container's start-up. Tell the human that before you start, and tell
  them the measured number afterwards. The budget the run is held to is 120 seconds.

## Steps

### 1. Confirm the human's go

Do not proceed without an explicit "yes, deploy" from the human for this specific
release.

### 2. Start the release

One command starts it. It runs against `main` unless told otherwise:

```
gh workflow run deploy-hosted-gateway.yml --repo thefrederiksen/devthrottle --ref main
```

A human can do the same thing by hand: GitHub -> Actions -> "Deploy hosted
Gateway" -> "Run workflow". Both are the same trigger.

### 3. Watch it run to the end

Find the run that just started and watch it. The whole thing takes roughly five
minutes, because it builds the container in Azure.

```
gh run list --repo thefrederiksen/devthrottle --workflow deploy-hosted-gateway.yml -L 1
gh run watch <run-id> --repo thefrederiksen/devthrottle --interval 30 --exit-status
```

The run itself finishes with its own health check, so a green run already means it
came back up. If the run fails at "Azure login", the deploy trust is broken - see
Troubleshooting.

### 4. Confirm the live service is healthy

```
curl -s -m 20 -o /dev/null -w "HTTP %{http_code}\n" https://devthrottle-gw.azurewebsites.net/healthz
```

A `200` means the live Gateway is up.

**A gap here IS expected now, and it is about a minute.** This section used to say
the deploy was a warmed slot swap with no cold start on the user path. That was true
until 20 September 2026 and is not true any more.

The plan was S1 Standard, whose single worker had 1.75 GB of memory and spent most of
its life swapping to disk. Deploys on it measured 87s, 122.8s, 172.0s and 360.7s of
outage - the warmed swap stopped saving anything, because both slots shared that one
starved worker. The owner moved the plan to Basic B2: twice the memory and twice the
processor for $25 a month instead of $69. Basic has no deployment slots, so the staging
slot was deleted and the deploy became an in-place restart.

**What normal looks like now:** the plan change itself, measured with a one-second
probe, took production down for **about 60 seconds**. A deploy should be in that
region. The run fails if it exceeds **120 seconds**, which is the owner's ruling of
20 September 2026: "It is completely okay to have two minutes out of this if we just
know we have it."

**What is still NOT normal:** minutes of `502`/`000` beyond that, or a site that never
comes back. Two deploys in August took the service down for 38.5s and 46.7s against a
five-second budget, both because a container failed its own startup and the platform
reverted by stopping the SITE - which tore down the healthy container beside it. The
same failure is available to an in-place restart. If `/healthz` does not answer `200`
within a couple of minutes of the run going green, say so; do not wait it out.

**A failed run is a report, not a protection.** The watch job fails when the outage
exceeds the budget, and it did fail on 12 August - users were dark for 46.7 seconds
regardless. Never present a green run as proof that deploying is free.

**If a deploy ships bad code**, roll back by putting a previous image back. The registry
keeps the **three newest** images, plus whatever production is running and the last one
that passed a whole deploy (`last-known-good`). The deploy run's summary lists what is
available, and prints the commit it replaced ("Outgoing live commit"):

```
gh workflow run rollback-hosted-gateway.yml --repo thefrederiksen/devthrottle --ref main -f commit=last-known-good
```

`commit=<short commit>` works too. That is a pin and a restart, not a rebuild - a minute
or two, and an outage of its own. It puts back CODE, not data: an older image does not
undo a database change that shipped with the bad deploy.

**The way back to zero-outage deploys** is a slot-capable plan (Premium v3 P0v3: 4 GB,
about $57 a month, cheaper than the old S1) plus the warmed-swap deploy and swap-back
rollback, which are in git history before 20 September 2026. That is the owner's call,
because it costs money every month.

### 5. Report plainly

Tell the human, in plain language, that the live Gateway was updated and is
healthy. Describe what changed, not run numbers.

## Troubleshooting

- **Run fails in about 20 seconds at "Azure login (OIDC)".** The Azure trust that
  lets GitHub deploy is missing or wrong. The deploy signs in as an Azure app
  registration that must carry a federated credential whose subject is
  `repo:thefrederiksen/devthrottle:environment:hosted-gateway-production`. Fixing
  this needs Azure access in the tenant that owns that app registration; bring the
  human in. Full context: the deploy workflow's own header comments in
  `.github/workflows/deploy-hosted-gateway.yml`, and the provisioning runbook at
  `docs/architecture/step3-azure-deploy` in the `devthrottle_internal` repository.
- **Health check never reaches 200 after several minutes.** The container is
  failing to start. Read the App Service logs in Azure (the human, or an agent
  authenticated to the Gateway subscription, can pull them). A failed database
  migration on startup is the usual cause.

## What this skill does not do

- It does not release the desktop app (`release-manager`).
- It does not provision or change Azure infrastructure or app settings.
- It does not auto-deploy on a commit. The live Gateway updates only when a person
  deliberately runs this. Committing to main never touches the live service.
