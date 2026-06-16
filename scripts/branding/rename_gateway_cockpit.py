"""
Rename Gateway + Cockpit from cc-director-* to devthrottle-* (asset + exe + assembly).

Flips "cc-director-gateway" -> "devthrottle-gateway" and "cc-director-cockpit" ->
"devthrottle-cockpit" across an explicit allowlist. PROTECTS the scheduled-task name
"cc-director-gateway-launch" (an upgrade-survival identifier that stays). Does NOT touch
the Director assets (cc-director-win-x64 / -mac-arm64), the install dir, or the registry key.

Files needing keep-old-AND-add-new semantics (ComponentRegistry exclusion list, the two
process stop-lists) are EXCLUDED here and edited by hand.
"""
import os

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LAUNCH = "cc-director-gateway-launch"
SENT = "KEEP_LAUNCH_TASK"

ALLOWLIST = [
    # exe + assembly names
    "src/CcDirector.GatewayApp/CcDirector.GatewayApp.csproj",
    "src/CcDirector.GatewayApp/GatewayTrayController.cs",            # avares URI
    "src/CcDirector.Cockpit/CcDirector.Cockpit.csproj",
    "src/CcDirector.Cockpit/Components/App.razor",                  # Blazor styles bundle link
    # engine paths / asset names
    "tools/cc-director-setup-engine/InstallLayout.cs",
    "tools/cc-director-setup-engine/GatewayUpdater.cs",
    "tools/cc-director-setup-engine/CockpitPackage.cs",
    # release pipeline
    ".github/workflows/release.yml",
    # tests (fixtures + path assertions)
    "tools/cc-director-setup-engine.Tests/UpdatePlannerTests.cs",
    "tools/cc-director-setup-engine.Tests/GatewaySelfUpdateTests.cs",
    "tools/cc-director-setup-engine.Tests/GatewayAndPinsTests.cs",
    "tools/cc-director-setup-engine.Tests/InstallLayoutTests.cs",
    "tools/cc-director-setup-engine.Tests/ComponentRegistryTests.cs",
    "tools/cc-director-setup-engine.Tests/CockpitUpdaterTests.cs",
    "tools/cc-director-setup-engine.Tests/CockpitPackageTests.cs",
    # active dev scripts
    "scripts/deploy-cockpit.ps1",
    "scripts/redeploy-gateway.ps1",
    "scripts/verify-gateway.ps1",
    # src: cockpit supervisor process name (critical), messages, UA, comments
    "src/CcDirector.Gateway/Cockpit/CockpitSupervisor.cs",
    "src/CcDirector.Gateway/Program.cs",
    "src/CcDirector.Gateway/CcDirector.Gateway.csproj",
    "src/CcDirector.Gateway.Tests/CockpitSupervisorTests.cs",
    "src/CcDirector.Avalonia/MainWindow.axaml.cs",
    "src/CcDirector.Cockpit/Services/GitHubItemStatusClient.cs",
]

total = 0
for rel in ALLOWLIST:
    path = os.path.join(REPO, rel)
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    orig = text
    text = text.replace(LAUNCH, SENT)                          # protect task name
    text = text.replace("cc-director-gateway", "devthrottle-gateway")
    text = text.replace("cc-director-cockpit", "devthrottle-cockpit")
    text = text.replace(SENT, LAUNCH)                          # restore task name
    n = sum(orig.count(x) for x in ("cc-director-gateway", "cc-director-cockpit")) - \
        sum(text.count(x) for x in ("cc-director-gateway", "cc-director-cockpit"))
    if text != orig:
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    total += n
    print(f"{n:3d}  {rel}")
print(f"TOTAL gateway/cockpit refs flipped: {total}")
