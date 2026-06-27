# cc-vault

Personal Vault CLI - manage contacts, tasks, goals, ideas, documents, and health data from the command line with RAG-powered search.

## Features

- **Contacts**: Store and manage contact information with interaction history
- **Tasks**: Track tasks with due dates, priorities, and contact/goal links
- **Goals**: Set and monitor goals with progress tracking
- **Ideas**: Capture and categorize ideas
- **Documents**: Import and search documents (Word, PDF, Markdown, text)
- **Health**: Track health data with AI-powered insights
- **RAG Search**: Ask questions and get AI-powered answers using your vault data

## Installation

### From Source

```bash
cd tools/cc-vault
pip install -e ".[full]"
```

### Build Executable

```powershell
.\build.ps1
copy dist\cc-vault.exe %LOCALAPPDATA%\cc-director\bin\
```

## Configuration

Set the vault path (optional, defaults to `%LOCALAPPDATA%\cc-director\vault`):

```bash
# Environment variable (highest precedence)
set CC_VAULT_PATH=D:\MyVault

# Or persist it by initializing into a specific path:
cc-vault init D:\MyVault
```

`cc-vault init <path>` writes the chosen path to the tool config file
(`%LOCALAPPDATA%\cc-director\config\vault\config.json`, key `vault_path`), and
later commands resolve the vault from `CC_VAULT_PATH` first, then that file, then
the default.

Set OpenAI API key for RAG features:

```bash
set OPENAI_API_KEY=sk-your-api-key
```

## Quick Start

```bash
# Initialize vault
cc-vault init

# Check vault stats
cc-vault stats

# Add a contact
cc-vault contacts add "John Doe" -e john@example.com -c "Acme Corp"

# Add a task
cc-vault tasks add "Follow up with John" -d 2026-02-25 -p high

# Add a goal
cc-vault goals add "Complete project" -t 2026-03-01

# Add an idea
cc-vault ideas add "Build a new feature for the app" -c product

# Import a document
cc-vault docs add document.pdf -t research

# Ask a question (RAG)
cc-vault ask "What tasks do I have this week?"

# Search documents
cc-vault search "project requirements" --hybrid
```

## Commands

### Main Commands

| Command | Description |
|---------|-------------|
| `init [path]` | Initialize a new vault |
| `stats` | Show vault statistics |
| `ask <question>` | Ask a question using RAG |
| `search <query>` | Search the vault |
| `backup` | Create a database backup |

### Tasks

| Command | Description |
|---------|-------------|
| `tasks list` | List tasks |
| `tasks add <title>` | Add a new task |
| `tasks done <id>` | Mark task as completed |
| `tasks cancel <id>` | Cancel a task |

### Goals

| Command | Description |
|---------|-------------|
| `goals list` | List goals |
| `goals add <title>` | Add a new goal |
| `goals achieve <id>` | Mark goal as achieved |
| `goals pause <id>` | Pause a goal |
| `goals resume <id>` | Resume a paused goal |
| `goals progress <id> <percent>` | Update goal progress |

### Ideas

| Command | Description |
|---------|-------------|
| `ideas list` | List ideas |
| `ideas add <content>` | Add a new idea |
| `ideas actionable <id>` | Mark idea as actionable |
| `ideas archive <id>` | Archive an idea |

### Contacts

| Command | Description |
|---------|-------------|
| `contacts list` | List contacts |
| `contacts add <name>` | Add a new contact |
| `contacts show <id>` | Show contact details |
| `contacts update <id>` | Update a contact |
| `contacts memory <id> <text>` | Add memory about contact |

### Documents

| Command | Description |
|---------|-------------|
| `docs list` | List documents |
| `docs add <path>` | Import a document |
| `docs show <id>` | Show document details |
| `docs search <query>` | Full-text search documents |

### Health

| Command | Description |
|---------|-------------|
| `health list` | List health entries |
| `health insights` | Get AI health insights |

### Config

| Command | Description |
|---------|-------------|
| `config show` | Show configuration |
| `config set <key> <value>` | Set a config value |

## Dependencies

### Required
- Python 3.11+
- typer, rich (CLI)
- openai, tiktoken (RAG)

### Optional
- python-docx (Word documents)
- pymupdf (PDF documents)

Vector/semantic search is built on native SQLite vector storage and does not
require ChromaDB.

## Architecture

Vector/semantic search uses native SQLite vector storage (the `vec_embeddings`
table in `vault.db`); it no longer depends on ChromaDB.

```
%LOCALAPPDATA%\cc-director\vault\
    vault.db              # SQLite database (structured data + vector embeddings)
    vectors/              # Legacy vector index (rebuildable via repair-vectors)
    documents/
        transcripts/
        notes/
        journals/
        research/
    health/
        daily/
        sleep/
        workouts/
    imports/              # Staging directory
    backups/
```

## License

MIT License - CC Director Contributors
