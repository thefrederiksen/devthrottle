# Reporting a bug and building DevThrottle

This page tells you how to report a bug and how to build DevThrottle from this repository.

## Reporting a bug

Open an [issue](https://github.com/thefrederiksen/devthrottle/issues). Say what you did, what you expected, and what happened instead, and include the version you are running and your operating system. A screenshot helps for anything visual.

Security problems are different: see [SECURITY.md](SECURITY.md), and please do not open a public issue for one.

## Building it

You need the **.NET 10 SDK** (`global.json` pins 10.0.403) and, for the browser front ends, **Node.js 20 or later**. Nothing else -- no administrator rights, no Docker, no database.

```powershell
git clone https://github.com/thefrederiksen/devthrottle
cd devthrottle
dotnet build cc-director.sln
```

The desktop application is Windows and macOS (Apple Silicon). The gateway, the cockpit, and the mobile application build anywhere.

## Running the tests

One command runs the default test suites:

```powershell
.\scripts\test-local.ps1
```

Three heavier suites are held back from the default run. To run them as well:

```powershell
.\scripts\test-local.ps1 -Parked
```

For the browser front ends:

```bash
npm install
npm run typecheck
npm run lint
```
