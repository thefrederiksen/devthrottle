# Plan: Voice Recorder page (two tabs) + Dictionary editor on the Gateway

Status: proposed
Owner: Soren
Date: 2026-05-24

## Goal

Group the existing Transcripts page and a new Dictionary editor under a single
"Voice Recorder" section on the Gateway, with a two-tab switcher. The Dictionary
tab gives a UI for the glossary that today can only be edited by hand-editing a
YAML file.

## Background: where the dictionary lives today

- File: `%LOCALAPPDATA%\cc-director\dictation\dictionary.yaml`
- Parsed by `DictionaryLoader` (`src/CcDirector.Core/Dictation/DictionaryLoader.cs`),
  shaped by `DictationDictionary` (`Models/Dictionary.cs`).
- Three sections:
  - `vocabulary` - canonical terms, packed into the OpenAI STT `prompt` bias.
  - `common_mistranscriptions` - correct term -> wrong spellings seen in practice,
    fed to the cleanup LLM.
  - `profiles` - named cleanup modes (`default`, `code`, `email`) with
    `cleanup_enabled` + optional `style_prompt`.
- SHARED resource: both the phone-recording transcriber
  (`OpenAiRecordingTranscriber`, wired in `RecordingEndpoints.cs:276-280`) and the
  desktop dictation (voice-to-type) feature read this same file. Editing it
  affects both. The page copy must state this.
- Hot reload: `DictionaryLoader` watches the file via `FileSystemWatcher`, and the
  recording transcriber is constructed per request, so edits take effect on the
  next recording / next dictation with no restart.

## Scope

In scope:
1. Rename the dashboard nav link `transcripts` -> `voice`.
2. Add a two-tab shell ("Transcripts" | "Dictionary") shared by both subpages.
3. New Dictionary editor page + read/write endpoints.
4. A YAML writer in `DictionaryLoader` (it currently only reads).

Out of scope (not asked for):
- Per-recording dictionary overrides.
- Importing terms from existing transcripts automatically.
- Auth / access changes (single-user tailnet).

## Routes and nav

- `/voice` -> redirects to `/transcripts` (default tab).
- `/transcripts` -> Transcripts tab (existing page, wrapped in the new shell).
- `/dictionary` -> Dictionary tab (new page).
- Dashboard top bar (`directory.html:251`): change
  `<a ... href="/transcripts">transcripts</a>` to
  `<a ... href="/voice">voice</a>`.

Both pages render the same tab strip at top:

```
+-----------------------------------------------------------------------+
|  Voice Recorder                                       <- Dashboard     |
|  ( Transcripts )  ( Dictionary )                                       |
+-----------------------------------------------------------------------+
```

## New API endpoints (in RecordingEndpoints.cs, alongside the /ingest/* group)

- `GET /ingest/dictionary`
  - Returns the parsed dictionary as JSON:
    `{ vocabulary: [..], commonMistranscriptions: { term: [variants] }, profiles: { name: { cleanupEnabled, stylePrompt } } }`
  - Reads `%LOCALAPPDATA%\cc-director\dictation\dictionary.yaml`. Missing file
    returns the empty shape (not an error).
- `PUT /ingest/dictionary`
  - Accepts the same JSON shape, validates it, serializes to YAML, writes the
    file atomically (temp file + move), returns the re-parsed result.
  - No partial/merge semantics: the page sends the whole document. Keeps the
    file as the single source of truth and avoids drift.

Share the dictionary-path resolution with the transcriber wiring so there is one
definition of the path (extract a small helper, e.g.
`DictationPaths.DictionaryFile(localAppData)`).

## Code changes

1. `src/CcDirector.Core/Dictation/DictionaryLoader.cs`
   - Add `static string Serialize(DictationDictionary dict)` using YamlDotNet's
     `SerializerBuilder` with `UnderscoredNamingConvention` (mirror of `Parse`).
   - Add `static void WriteToDisk(string path, DictationDictionary dict)` doing an
     atomic write (write temp, `File.Move(..., overwrite:true)`), creating the
     `dictation` directory if absent.
   - Unit tests: round-trip Parse(Serialize(x)) == x for vocabulary, patterns,
     profiles, and the always-present `default` profile.

2. `src/CcDirector.Gateway/Api/RecordingEndpoints.cs`
   - Add the `GET /ingest/dictionary` and `PUT /ingest/dictionary` handlers.
   - Add `GET /voice` (redirect to `/transcripts`) and `GET /dictionary`
     (serve `dictionary.html`).
   - Extract the dictionary path into a shared helper (used by both the
     transcriber wiring and the new endpoints).

3. `src/CcDirector.Gateway/Web/dictionary.html` (new)
   - Dark card aesthetic copied from `transcripts.html` (same CSS variables).
   - Tab strip at top (Transcripts | Dictionary), Dictionary active.
   - Three editable sections (vocabulary chips, mistranscription rows,
     profile cards) per the mock below.
   - GET on load, PUT on Save. A single top-right Save button with a "Saved"
     confirmation, matching the Transcripts page idiom.

4. `src/CcDirector.Gateway/Web/transcripts.html`
   - Add the same tab strip header so the two pages feel like one section.
   - Change the "Transcripts" h1 area to the shared "Voice Recorder" + tabs.

5. `src/CcDirector.Gateway/CcDirector.Gateway.csproj`
   - Add `<EmbeddedResource Include="Web\dictionary.html" />` (line ~31, next to
     the other Web pages).

6. `src/CcDirector.Gateway/Web/directory.html`
   - Nav link rename (line 251).

## Dictionary page layout (target)

```
+=======================================================================+
|  Voice Recorder                                       <- Dashboard     |
|  ( Transcripts )  [ Dictionary ]                                       |
+=======================================================================+
|  One shared glossary. Used by phone-recording transcription AND        |
|  desktop voice-to-type. Edits apply on the next recording.  [ Save ]   |
+-----------------------------------------------------------------------+
|  VOCABULARY                  terms biased into speech-to-text          |
|  +-----------------------------------------------------------------+  |
|  |  [ mindzie x ] [ CenCon x ] [ ConPTY x ] [ cc-director x ]      |  |
|  |  [ Avalonia x ] [ Soren Frederiksen x ]                        |  |
|  |  [ + add term....................................... ]          |  |
|  +-----------------------------------------------------------------+  |
|                                                                       |
|  COMMON MISTRANSCRIPTIONS    correct term  <-  wrong spellings seen    |
|  +-----------------------------------------------------------------+  |
|  |  mindzie       <-  [Minzy x][Mindsy x][Mindzy x][Mindzie x] [+] |  |
|  |  CenCon        <-  [SenCon x][SENCON x][Sencon x]           [+] |  |
|  |  ConPTY        <-  [Contui x][ContUI x][Conty x]            [+] |  |
|  |  [ + add a term to correct........................ ]           |  |
|  +-----------------------------------------------------------------+  |
|                                                                       |
|  PROFILES                    cleanup behavior per context             |
|  +-------------------+  +-------------------+  +-------------------+   |
|  | default           |  | code              |  | email             |   |
|  | cleanup:  [ ON  ] |  | cleanup:  [ OFF ] |  | cleanup:  [ ON  ] |   |
|  | style: (none)     |  | style: (none)     |  | style: "tighten   |   |
|  +-------------------+  +-------------------+  +-------------------+   |
|                                                   [ + add profile ]   |
+-----------------------------------------------------------------------+
```

## Testing

- `DictionaryLoader` round-trip unit tests (Parse/Serialize/Write).
- Gateway endpoint tests: GET returns current file; PUT writes and re-reads;
  PUT with malformed body returns 400; missing file GET returns empty shape.
- Manual: edit a term on the page, save, record a phone clip, confirm the new
  term biases the transcript.

## Risks / notes

- The dictionary is shared with desktop dictation; a careless edit affects both.
  Mitigated by the page copy and by keeping the whole-document PUT (no silent
  partial writes).
- YAML serialization must preserve the `default` profile guarantee that `Parse`
  enforces; covered by the round-trip test.
```
