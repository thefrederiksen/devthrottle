"""
One-shot controlled rebrand of USER-FACING setup strings: "CC Director" -> "DevThrottle".

Operates ONLY on an explicit allowlist of files that carry user-facing setup
branding. Deliberately EXCLUDES files where "CC Director" is a stable identifier
that must not change (installed mac bundle name, registry key, PATH marker).
Prints per-file replacement counts. ASCII-only output.
"""
import os

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OLD = "CC Director"
NEW = "DevThrottle"

# Files with user-facing branding to rebrand. Excludes MacAppPlacer.cs,
# InstallLayout.cs, PathManager.cs, avalonia EngineInstallRunner.cs (those keep
# "CC Director.app" / PATH markers), and out-of-scope comments/tool metadata.
ALLOWLIST = [
    # WPF setup wizard
    "tools/cc-director-setup/MainWindow.xaml",
    "tools/cc-director-setup/MainWindow.xaml.cs",
    "tools/cc-director-setup/Steps/WelcomeStep.xaml",
    "tools/cc-director-setup/Steps/WelcomeStep.xaml.cs",
    "tools/cc-director-setup/Steps/UninstallStep.xaml",
    "tools/cc-director-setup/Steps/UninstallStep.xaml.cs",
    "tools/cc-director-setup/Steps/InstallStep.xaml",
    "tools/cc-director-setup/Steps/CompleteStep.xaml",
    "tools/cc-director-setup/Steps/CompleteStep.xaml.cs",
    "tools/cc-director-setup/Services/ShortcutCreator.cs",
    "tools/cc-director-setup/Services/EngineInstallRunner.cs",
    # Avalonia setup wizard (mac) - user-facing only
    "tools/cc-director-setup-avalonia/MainWindow.axaml",
    "tools/cc-director-setup-avalonia/MainWindow.axaml.cs",
    "tools/cc-director-setup-avalonia/Steps/WelcomeStep.axaml",
    "tools/cc-director-setup-avalonia/Steps/WelcomeStep.axaml.cs",
    "tools/cc-director-setup-avalonia/Steps/InstallStep.axaml",
    "tools/cc-director-setup-avalonia/Steps/CompleteStep.axaml",
    "tools/cc-director-setup-avalonia/Steps/CompleteStep.axaml.cs",
    "tools/cc-director-setup-avalonia/Services/ShortcutCreator.cs",
    # Engine (display name, publisher, shortcut, uninstall) - NOT MacAppPlacer/InstallLayout
    "tools/cc-director-setup-engine/AddRemovePrograms.cs",
    "tools/cc-director-setup-engine/InstallFinalizer.cs",
    "tools/cc-director-setup-engine/Uninstaller.cs",
    "tools/cc-director-setup-engine/ComponentRegistry.cs",
    # Test that asserts the ARP DisplayName
    "tools/cc-director-setup-engine.Tests/AddRemoveProgramsTests.cs",
    # CLI help text
    "tools/cc-director-setup-cli/Program.cs",
]

total = 0
for rel in ALLOWLIST:
    path = os.path.join(REPO, rel)
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    n = text.count(OLD)
    if n:
        text = text.replace(OLD, NEW)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    total += n
    print(f"{n:3d}  {rel}")
print(f"TOTAL replacements: {total}")
