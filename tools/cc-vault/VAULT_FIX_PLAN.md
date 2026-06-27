# cc-vault Stability Fix Plan

> **STATUS: SUPERSEDED / HISTORICAL (kept for reference).**
> This plan was written around a ChromaDB-based vector design that has since been
> **replaced by native SQLite vector storage** (`src/vectors.py`, the
> `vec_embeddings` table in `vault.db`). The ChromaDB segfault problem it targets
> no longer applies, and most phases here are already implemented (repair-vectors,
> backup --list, restore). Read this only for background; do not treat the
> ChromaDB instructions or the old `cc-myvault` path as current. The real vault
> path is `%LOCALAPPDATA%\cc-director\vault`.

**Date:** 2026-02-27
**Priority:** CRITICAL -- vault segfaults on search/ask commands, eroding user trust
**Goal:** Make the vault reliable. No silent failures, no segfaults, no data loss.

---

## Context

The vault is a personal data platform storing 4,584 contacts, 462 documents, 32 ideas, and mailing lists. It uses SQLite for structured data and (historically) ChromaDB for vector/semantic search. The problem this plan addressed: any command that touched ChromaDB (search, ask) segfaulted, while SQLite-only commands (stats, lists, contacts) worked fine. Vector search has since moved to native SQLite, removing the ChromaDB dependency entirely.

**Architecture:**
- CLI: `src/cli.py` (Typer) -> commands
- Database: `src/db.py` (SQLite, vault.db)
- Vectors: `src/vectors.py` (native SQLite vectors + OpenAI embeddings; formerly ChromaDB)
- RAG: `src/rag.py` (hybrid search + OpenAI chat completions)
- Config: `src/config.py` (paths, API keys, constants)
- Vault path: `%LOCALAPPDATA%\cc-director\vault\`
- Deployed as: PyInstaller exe

---

## Phase 1: Stop the Segfaults (CRITICAL)

### 1.1 Wrap ChromaDB initialization in try/except

**File:** `src/vectors.py`, lines 55-59

Current code:
```python
self.client = chromadb.PersistentClient(
    path=str(self.persist_directory),
    settings=Settings(anonymized_telemetry=False)
)
```

Fix: Wrap in try/except. If ChromaDB fails to initialize, set `self.client = None` and log a clear error. All methods that use `self.client` must check for None first and raise a clear RuntimeError ("Vector store unavailable -- run cc-vault repair-vectors").

### 1.2 Add timeout to OpenAI client initialization

**File:** `src/vectors.py`, line 64

Current:
```python
self.openai_client = openai.OpenAI(api_key=OPENAI_API_KEY)
```

Fix:
```python
self.openai_client = openai.OpenAI(api_key=OPENAI_API_KEY, timeout=30.0)
```

**File:** `src/rag.py`, line 47 -- same fix needed there.

### 1.3 Add try/except to all OpenAI API calls

These locations have bare OpenAI calls with no exception handling:

| File | Lines | Method | Call |
|------|-------|--------|------|
| vectors.py | 83-87 | embed_text() | embeddings.create() |
| vectors.py | 94-98 | embed_texts() | embeddings.create() |
| rag.py | 217-225 | ask() | chat.completions.create() |
| rag.py | 301-309 | summarize() | chat.completions.create() |
| rag.py | 404-412 | health_insights() | chat.completions.create() |

Catch these exceptions:
- `openai.APIConnectionError` -- network problem
- `openai.RateLimitError` -- quota exceeded
- `openai.AuthenticationError` -- bad API key
- `openai.APIStatusError` -- server error
- `openai.APITimeoutError` -- timeout

On catch: log the error, return a clear error message to the user. Do NOT silently swallow.

### 1.4 Validate OpenAI response structure before accessing

**File:** `rag.py`, line 227:
```python
answer = response.choices[0].message.content
```

Fix: Check `response.choices` is non-empty before indexing. Same at lines 311 and 415.

**File:** `vectors.py`, lines 87, 98:
```python
return response.data[0].embedding
```

Fix: Check `response.data` is non-empty before indexing.

### 1.5 Protect ChromaDB query result parsing

**File:** `vectors.py`, lines 141-158 (query_documents), and similar blocks at lines 250-257, 537-544, 593-600, 621-629.

Current code assumes `results['ids']`, `results['documents']`, and `results['distances']` all have the same length. Wrap in try/except IndexError and validate lengths match before iterating.

---

## Phase 2: Graceful Degradation for Search

### 2.1 Make search work without ChromaDB

When ChromaDB is unavailable (failed init, corrupted vectors, segfault-prone), the `search` and `ask` commands should fall back to SQLite FTS5-only search instead of crashing.

**File:** `src/vectors.py`, method `hybrid_search()` (line 275)

Current flow:
1. Get vector results from ChromaDB
2. Get FTS5 results from SQLite
3. Merge with weighted scoring

Fix: If ChromaDB is unavailable (self.client is None), skip step 1 and return FTS5 results only. Print a warning: "WARNING: Vector search unavailable, using text search only. Run cc-vault repair-vectors to fix."

### 2.2 Add a `repair-vectors` command

**File:** `src/cli.py` -- add new command

This command should:
1. Delete the `vectors/` directory entirely
2. Re-initialize ChromaDB from scratch
3. Re-index all document chunks from SQLite into ChromaDB
4. Print progress and final count

The data source of truth is SQLite. ChromaDB is a derived index that can always be rebuilt.

---

## Phase 3: Add Restore Command

### 3.1 Implement `cc-vault restore`

**File:** `src/cli.py` -- add new command

The backup command (line 248) creates zip files in `backups/`. There is NO restore command.

Implement:
```
cc-vault restore <backup-file.zip>
```

Steps:
1. Validate the zip file contains vault.db
2. Create a backup of the CURRENT vault first (safety net)
3. Extract the zip to the vault path, overwriting existing files
4. Run `init_db()` to apply any schema migrations
5. Print summary of restored data (contact count, document count, etc.)

### 3.2 List available backups

Add `cc-vault backup --list` to show available backup files with dates and sizes.

---

## Phase 4: Pin Dependency Versions

### 4.1 Fix requirements.txt

**File:** `requirements.txt`

Current (all use `>=` with no upper bound):
```
typer>=0.9.0
rich>=13.0.0
openai>=1.0.0
tiktoken>=0.5.0
chromadb>=0.4.0
python-docx>=0.8.0
pymupdf>=1.23.0
pytest>=7.0.0
pyinstaller>=6.0.0
```

Fix -- add upper bounds to prevent breaking changes:
```
typer>=0.9.0,<1.0.0
rich>=13.0.0,<14.0.0
openai>=1.0.0,<2.0.0
tiktoken>=0.5.0,<1.0.0
chromadb>=0.4.0,<2.0.0
python-docx>=0.8.0,<2.0.0
pymupdf>=1.23.0,<2.0.0
pytest>=7.0.0,<9.0.0
pyinstaller>=6.0.0,<7.0.0
```

---

## Phase 5: Error Handling Cleanup

### 5.1 ChromaDB exception handling is too narrow

**File:** `src/vectors.py`

Multiple locations catch only `ValueError` from ChromaDB operations:
- Line 270 (delete_chunks_by_document)
- Line 706-728 (semantic_search collection iteration)

Fix: Catch `Exception` for ChromaDB operations (ChromaDB does not have a clean exception hierarchy). Log the actual error class and message. Do not silently return empty results -- at minimum log a warning.

### 5.2 JSON serialization safety

**File:** `src/cli.py`, lines 1482, 1519, 1553, 1610, 1662, 1711, 1718, 1744

Multiple places do:
```python
console.print(json.dumps(result, indent=2))
```

Add `default=str` to handle datetime and other non-serializable objects:
```python
console.print(json.dumps(result, indent=2, default=str))
```

---

## Rules for the Developer

1. **NO FALLBACK PROGRAMMING** -- Do not add "try X, fall back to Y" patterns for things that should work. If ChromaDB is broken, tell the user clearly and point them to `repair-vectors`. The ONE exception is search: FTS5 fallback is acceptable because it still returns useful results.

2. **NO UNICODE/EMOJI** -- All print statements, error messages, and log output must use ASCII only. Use `[+]`, `[-]`, `[!]` prefixes, not checkmarks or warning symbols.

3. **Clear error messages** -- Every error message must say what went wrong AND what the user should do about it. Example: "ERROR: ChromaDB failed to initialize. Run: cc-vault repair-vectors"

4. **Do not add unnecessary abstractions** -- Fix the specific issues listed. Do not refactor working code, add base classes, or create utility modules.

5. **Test with the deployed exe** -- After changes, build with `build.ps1` and test the actual exe, not just `python -m`. PyInstaller bundling can behave differently.

---

## Files to Modify

| File | Changes |
|------|---------|
| src/vectors.py | ChromaDB init safety, OpenAI timeouts, exception handling, response validation |
| src/rag.py | OpenAI timeouts, exception handling, response validation |
| src/cli.py | Add repair-vectors command, add restore command, add backup --list, JSON safety |
| requirements.txt | Pin upper bounds on all dependencies |

## Files NOT to Modify

- src/db.py -- SQLite layer is working correctly, leave it alone
- src/config.py -- Configuration is fine
- src/chunker.py -- Chunking logic is fine
- src/graph.py -- Graph logic is fine
- src/importer.py -- Import logic is fine (unless you find a bug during testing)

---

## Verification

After all fixes, these commands must work without crashing:

```bash
cc-vault stats                    # Should work (already works)
cc-vault lists                    # Should work (already works)
cc-vault lists show "Toronto Course"  # Should work (already works)
cc-vault search "toronto"         # MUST NOT segfault -- use FTS5 if vectors broken
cc-vault ask "who lives in toronto"   # MUST NOT segfault -- clear error if vectors broken
cc-vault repair-vectors           # Should rebuild vector index from SQLite
cc-vault backup                   # Should create zip backup
cc-vault restore <file>           # Should restore from zip backup
```
