# DevThrottle Mobile

A React + TypeScript Progressive Web App, served by the Gateway at `/m`. This is the foundation
(Issue 1) of the mobile app plan in `docs/architecture/mobile/`.

## Build

```bash
npm ci
npm run build      # type-checks, then builds the static app into dist/
```

`dist/` is static files only. There is no runtime Node dependency: the Gateway serves the built
files, and the release pipeline (an MSBuild target on `CcDirector.Gateway.csproj`, gated to a
publish/release configuration) runs `npm ci && npm run build` and copies `dist/**` into the
Gateway's `wwwroot/m/`. A routine `dotnet build` does NOT run npm.

## Typed API client

The Gateway emits an OpenAPI document at `/openapi/v1.json`. The TypeScript types in
`src/api/schema.ts` are generated from it so the C# DTOs stay the single source of truth:

```bash
# With a Gateway running on 127.0.0.1:7878 (adjust the URL/port as needed):
npm run gen:api
```

A C# DTO change that is not reflected in a regenerated `schema.ts` fails the TypeScript build.

## Local dev

```bash
npm run dev        # Vite dev server; proxy /sessions to a running Gateway as needed
```

## Layout

- `src/api/` - the typed Gateway client (`client.ts`) and generated types (`schema.ts`).
- `src/sessions/ordering.ts` - the triage/ordering port of the C# `SessionOrdering` policy.
- `src/pages/Home.tsx` - the Home roster ("needs you" group + full session list).
- `src/pages/SessionDetail.tsx` - the minimal session-detail placeholder (Terminal/Chat land later).
