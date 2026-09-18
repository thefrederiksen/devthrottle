# cc-outlook

Outlook CLI for Claude Code: read, send, search emails and manage calendar from the command line.

## Installation

1. Build the executable:
   ```powershell
   cd src\cc-outlook
   .\build.ps1
   ```

2. The executable will be at `dist\cc-outlook.exe`

3. Run the central build script to copy to `%LOCALAPPDATA%\cc-director\bin\`:
   ```batch
   scripts\build.bat
   ```

## Quick Start

### 1. Set Up Azure App (One-Time)

1. Go to https://portal.azure.com
2. Search for "App registrations" -> "+ New registration"
3. Settings:
   - Name: `cc-outlook_cli`
   - Account types: "Accounts in any organizational directory and personal Microsoft accounts"
   - Redirect URI: Mobile and desktop -> `https://login.microsoftonline.com/common/oauth2/nativeclient`
4. Copy the Application (client) ID
5. Go to API permissions -> Add: Mail.ReadWrite, Mail.Send, Calendars.ReadWrite, User.Read, MailboxSettings.Read
6. Go to Authentication -> Enable "Allow public client flows"

See [docs/AUTHENTICATION.md](docs/AUTHENTICATION.md) for detailed setup instructions.

### 2. Add Your Account

```bash
cc-outlook accounts add your@email.com --client-id YOUR_CLIENT_ID
```

### 3. Authenticate (Device Code Flow)

```bash
cc-outlook auth
```

1. A code will be displayed (e.g., `XXXXXXXXX`)
2. Go to https://microsoft.com/devicelogin
3. Enter the code and sign in
4. Authentication completes automatically in the CLI

## Usage

### Email Commands

```bash
# List emails
cc-outlook list                    # List inbox (default 10)
cc-outlook list -n 20              # List 20 messages
cc-outlook list -f sent            # List sent mail
cc-outlook list --unread           # Show unread only

# Read email
cc-outlook read <message_id>

# Send email
cc-outlook send -t "to@example.com" -s "Subject" -b "Body text"
cc-outlook send -t "to@example.com" -s "Subject" -b "Body" --cc "cc@example.com"
cc-outlook send -t "to@example.com" -s "Subject" -b "Body" --bcc "bcc@example.com"
cc-outlook send -t "to@example.com" -s "Subject" -b "<h1>HTML</h1>" --html
cc-outlook send -t "to@example.com" -s "Subject" -b "Urgent!" --importance high
cc-outlook send -t "to@example.com" -s "Subject" -b "Body" -a report.pdf -a notes.txt

# Draft for review before sending
cc-outlook draft -t "to@example.com" -s "Subject" -b "Body"
cc-outlook draft -t "to@example.com" -s "Subject" -b "Body" -a report.pdf  # Repeatable

# Reply/Forward
cc-outlook reply <message_id> -b "Thanks for the info"
cc-outlook reply <message_id> -b "Thanks all" --all  # Reply all
cc-outlook reply <message_id> -b "Attached" --attach report.pdf  # -a is --all here
cc-outlook forward <message_id> -t "other@example.com" -b "FYI"
cc-outlook forward <message_id> -t "other@example.com" -a extra.pdf

# Search
cc-outlook search "project update"

# Delete
cc-outlook delete <message_id>

# Flag and categorize
cc-outlook flag <message_id>                    # Flag for follow-up
cc-outlook flag <message_id> -s complete        # Mark complete
cc-outlook flag <message_id> -d 2024-12-31      # Flag with due date
cc-outlook categorize <message_id> "Work,Urgent"

# Attachments
cc-outlook attachments <message_id>                           # List attachments, with full IDs
cc-outlook attachments <message_id> --json                    # Same, as JSON
cc-outlook download-attachment <message_id> <attachment_id>   # Download to the current directory
cc-outlook download-attachment <message_id> <attachment_id> -o C:\out\invite.ics
cc-outlook download-attachment <message_id> <attachment_id> -o C:\out   # Into a directory

# Folders
cc-outlook folders
```

### Calendar Commands

```bash
# View events
cc-outlook calendar events         # Next 7 days
cc-outlook calendar events -d 14   # Next 14 days

# View event details
cc-outlook calendar get <event_id>

# Create event
cc-outlook calendar create -s "Meeting" -d 2024-12-25 -t 14:00
cc-outlook calendar create -s "Meeting" -d 2024-12-25 -t 14:00 --duration 90
cc-outlook calendar create -s "Meeting" -d 2024-12-25 -t 14:00 -l "Room 101"
cc-outlook calendar create -s "Meeting" -d 2024-12-25 -t 14:00 --attendees "a@ex.com,b@ex.com"
cc-outlook calendar create -s "Holiday" -d 2024-12-25 -t 00:00 --all-day

# Update event
cc-outlook calendar update <event_id> -s "New Subject"
cc-outlook calendar update <event_id> -l "New Location"
cc-outlook calendar update <event_id> -d 2024-12-26 -t 15:00

# Respond to invitations
cc-outlook calendar respond <event_id> accept
cc-outlook calendar respond <event_id> decline -m "Sorry, I'm busy"
cc-outlook calendar respond <event_id> tentative

# Delete event
cc-outlook calendar delete <event_id>
```

### Account Management

```bash
# List accounts
cc-outlook accounts list

# Add account
cc-outlook accounts add work@company.com --client-id YOUR_CLIENT_ID

# Set default
cc-outlook accounts default work@company.com

# Use specific account
cc-outlook -a work list
```

## Configuration

Configuration is stored in `~/.cc-outlook/`:

| File | Purpose |
|------|---------|
| `profiles.json` | Account configurations |
| `tokens/` | OAuth tokens |

## Troubleshooting

### "Failed to create device flow"

Ensure "Allow public client flows" is enabled in Azure app settings.

### Device code expired

The code is valid for ~15 minutes. Run `cc-outlook auth` again if it expires.

### Token expired

```bash
cc-outlook auth --force
```

See [docs/AUTHENTICATION.md](docs/AUTHENTICATION.md) for more troubleshooting.

## Development

### Project Structure

```
src/cc-outlook/
  src/
    __init__.py      # Version info
    __main__.py      # Module entry point
    cli.py           # Typer CLI commands
    auth.py          # Authentication logic
    outlook_api.py   # O365 API wrapper
    utils.py         # Helper functions
  docs/
    AUTHENTICATION.md
  tests/
  main.py            # PyInstaller entry point
  build.ps1          # Build script
  cc-outlook.spec    # PyInstaller spec
  pyproject.toml     # Package config
  requirements.txt   # Dependencies
```

### Building

```powershell
.\build.ps1
```

### Testing

```bash
pytest tests/
```

## License

MIT
