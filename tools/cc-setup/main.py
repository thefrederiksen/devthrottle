"""
cc-director-setup - Windows installer for cc-director tools suite
Downloads and installs all tool executables, adds to PATH, installs SKILL.md
"""

import sys
from installer import CCDirectorInstaller


def main():
    """Main entry point for the installer."""
    print("=" * 60)
    print("  cc-director Setup")
    print("  https://github.com/thefrederiksen/devthrottle")
    print("=" * 60)
    print()

    installer = CCDirectorInstaller()

    try:
        success = installer.install()
        if success:
            print()
            print("=" * 60)
            print("  Installation complete!")
            print("  Restart your terminal to use cc-director tools.")
            print("=" * 60)
            return 0
        else:
            print()
            print("Installation failed. See errors above.")
            return 1
    except KeyboardInterrupt:
        print()
        print("Installation cancelled by user.")
        return 1
    except (OSError, IOError) as e:
        print()
        print(f"ERROR: {e}")
        return 1
    except RuntimeError as e:
        print()
        print(f"ERROR: {e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
