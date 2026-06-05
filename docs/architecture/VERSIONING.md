# Versioning Strategy

**Status: AGREED 2026-06-05. This is the contract for how CC Director is versioned.**

## The model: lockstep product versioning

CC Director uses **one product version for everything**. Every release, every
component -- Director, Gateway, Cockpit, setup wizard, setup CLI, the Python
tools bundle -- ships at the same version, built from the same commit, even if a
component had zero code changes.

The version answers exactly one question: **"which release are you running?"**

Why lockstep and not per-component versions:

- All components live in one repo, are built by one pipeline from one commit,
  and are tested together. They should be versioned together.
- "Gateway 0.5.9 + Director 0.6.3 -- is that OK?" is a question nobody should
  ever have to answer. Lockstep makes the compatibility matrix disappear.
- One number on screen tells support/debugging the exact commit every binary
  came from.

The cost is re-shipping unchanged components. That cost is megabytes; the cost
of mixed versions is permanent cognitive load. We pay the megabytes.

## Single source of truth: Directory.Build.props

The version lives in **exactly one file**: `Directory.Build.props` at the repo
root.

```xml
<Project>
  <PropertyGroup>
    <Version>0.6.3</Version>
  </PropertyGroup>
</Project>
```

MSBuild automatically imports this into every `.csproj` in the tree. From that
one `<Version>`, the .NET SDK derives all three stamps baked into every binary:

| Property | What it is | Where you see it |
|---|---|---|
| `AssemblyVersion` | .NET assembly identity | Reflection |
| `FileVersion` | Win32 file version | Explorer -> Properties -> Details |
| `InformationalVersion` | Display string: semver **+ git commit SHA** | Product version field, all UIs |

The SDK appends the commit SHA to `InformationalVersion` automatically
(SourceLink, on by default since .NET 8): a binary built from `0.6.3` at commit
`1cc1abd...` carries `0.6.3+1cc1abd...`. Every binary self-identifies both its
release and its exact commit.

**Rules:**

- No `.csproj` may declare its own `<Version>` (a project-level value silently
  overrides the props file). The only exception is `archived/`.
- No version string is ever hardcoded in XAML/Razor/HTML. UIs **read** the
  version from their own assembly at runtime via
  `CcDirector.Core.AppVersion` (or the inline equivalent in projects that do
  not reference Core).

## How a release flows

1. Run `scripts/new-release.ps1`. It bumps **one file**
   (`Directory.Build.props`), commits `release: vX.Y.Z`, tags `vX.Y.Z`, pushes.
2. CI (`release.yml`) builds everything from the tagged commit. Every .NET
   binary is stamped `X.Y.Z` automatically. A guard step fails the release if
   the tag and `Directory.Build.props` disagree.
3. The release manifest lists every asset with its version (= the release
   version) and its SHA-256.
4. Installed machines see `X.Y.Z > installed` and update every asset in their
   role: Director self-update on launch, Gateway self-update with health-check
   rollback, tools via ToolUpdater.

## Where the version is shown

Readily visible, everywhere, read at runtime -- never hardcoded:

| Surface | What it shows |
|---|---|
| Director status bar (bottom) | `v0.6.3 (1cc1abd)`; tooltip: full version, build time, exe path |
| Director splash + Help dialog | `v0.6.3` |
| Gateway `/healthz`, `/` JSON | full informational version |
| Cockpit topbar | `v0.6.3` |
| Setup wizards (WPF + Avalonia) | `v0.6.3` from their own assembly |
| Setup CLI | `cc-director-setup-cli version` |
| Any exe in Explorer | Properties -> Details -> File/Product version |

The build *timestamp* (the old status-bar value) remains in the Director's
status-bar tooltip -- it is still the fastest way to confirm a local slot build
actually deployed.

## Versions are for humans; hashes are for machines

The updater decides "is this the same bits?" by **SHA-256, not by version**.
The manifest already carries `sha256` per asset, and every updater path
verifies it after download.

Planned follow-up (not yet built): record the applied asset's SHA-256 in
`installed.json` and have `UpdatePlanner` treat *version newer but SHA-256
identical* as up-to-date (bookkeeping-only version bump, no download). This
makes lockstep free for big, rarely-changing payloads -- in particular the
~333MB Python tools bundle, which today would re-download on every release.
Until that lands, the bundle re-downloads each release; acceptable at the
current fleet size, but build it before the fleet grows.

## What stays outside lockstep

- **Python tools' internal `pyproject.toml` versions** -- irrelevant plumbing.
  The *bundle* is the release asset, identified by the product version + hash.
- **`scripts/release-asset-versions.json`** -- the per-asset version override
  hook in `release.yml`. Reserved as an emergency escape hatch (e.g. pinning a
  broken asset to a prior version); under the lockstep model it should not
  exist in the repo in normal operation.

## Version skew during update

There is a brief window where e.g. the Director is `0.7.0` but the Gateway is
still `0.6.3` (each updates on its own trigger). We deliberately do **not**
engineer for this -- no compatibility matrices, no version negotiation. Two
rules keep it safe:

1. Wire contracts (`CcDirector.Gateway.Contracts`) evolve **additively**:
   add fields, never repurpose or remove ones in use.
2. Skew is made *visible* instead of managed: every surface shows its own
   version, so "ah, the Gateway hasn't updated yet" is one glance away.
