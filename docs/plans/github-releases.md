# GitHub Automated Releases Plan

## Goal

When you push a version tag (like `v1.2.0`), GitHub automatically builds everything and publishes downloadable .exe files on the GitHub Releases page.

## Key Decisions

1. **All .NET apps ship self-contained** -- .NET 10 is bundled into every exe. Users never install .NET separately. Bigger files but zero friction.

2. **One GitHub Actions workflow file** (`.github/workflows/release.yml`) that:
   - Triggers when you push a version tag
   - Runs on a free GitHub-provided Windows VM
   - Builds the main app, all Python tools, all .NET tools
   - Publishes everything to the Releases page automatically

3. **Fix setup downloads** to use the real GitHub Releases URL instead of the old repo reference

4. **Add a build orchestration script** so all 24 tool builds can run in sequence from one command (useful both locally and in the GitHub workflow)

5. **Update README** to point users to the GitHub Releases page for downloads

## Developer Experience

```bash
git tag v1.2.0
git push origin v1.2.0
# Done. Go grab coffee. Releases page has everything in ~30 min.
```

## Related Plans

- [Installer Wizard](installer-wizard.md) -- the self-contained WPF installer that downloads tools from these GitHub Releases
