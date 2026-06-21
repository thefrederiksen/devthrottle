# Handover: Expand SEND ALL to Multi-Platform Dispatch

## Background

The Communication Manager's **SEND ALL** button currently only dispatches **email** items. This task expands it to dispatch **all supported platforms**: email, LinkedIn, and Reddit. Future platforms (Twitter, Facebook, etc.) don't have tools yet and should be skipped with a log message.

The previous handover (`docs/handover-comm-dispatch.md`) describes what was built so far. This document picks up where it left off.

---

## Scope

All changes are in **one file**:

```
src/CcDirector.CommunicationManager/ViewModels/MainViewModel.cs
```

The Engine's `CommunicationDispatcher.cs` (auto-dispatch, currently disabled) is **deferred** to a follow-up task. Do not touch it.

---

## Current Code (What Exists Today)

### SendAllAsync (lines 635-689)
- Sets `IsSending = true`, calls `GetApprovedEmails(dbPath)`, iterates emails, calls `DispatchEmailAsync` per item, tracks sent/failed, updates status bar, calls `RefreshAsync()`.

### GetApprovedEmails (lines 691-745)
- Queries **only** `platform = 'email'` items from the `communications` table
- Parses `email_specific` JSON into `EmailSpecificDto`
- Returns `List<QueuedEmail>`

### DispatchEmailAsync (lines 747-813)
- Determines `cc-gmail` vs `cc-outlook` based on `send_from` / persona
- Builds CLI args using `ArgumentList` (safe)
- Runs process, reads stdout/stderr concurrently, checks exit code
- Calls `MarkPosted()` on success

### MarkPosted (lines 815-829)
- Updates `status = 'posted'`, sets `posted_at` and `posted_by = 'cc-director-manual'`
- **Already platform-agnostic** -- no changes needed

### QueuedEmail DTO (lines 831-843)
- Email-only: Id, TicketNumber, Body, To, Cc, Bcc, Subject, Attachments, Persona, SendFrom

### EmailSpecificDto (lines 845-852)
- JSON deserialization target: To, Cc, Bcc, Subject, Attachments

### PostToLinkedInAsync (lines 540-629)
- Existing single-item LinkedIn post method (separate from SendAll)
- Uses unsafe `Arguments` string concatenation (line 587) -- should be fixed to use `ArgumentList`
- Keep this method as a single-item action button

### Existing fields/statics (lines 631-632)
```csharp
private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
private static readonly HashSet<string> _gmailAccounts = new(StringComparer.OrdinalIgnoreCase) { "personal" };
```

---

## What To Build (Step by Step)

### Step 1: Replace `QueuedEmail` with `QueuedItem` (lines 831-843)

Replace the email-only DTO with a generic one:

```csharp
private class QueuedItem
{
    public string Id { get; set; } = "";
    public int TicketNumber { get; set; }
    public string Platform { get; set; } = "";
    public string Type { get; set; } = "";       // post, comment, reply, email
    public string Content { get; set; } = "";
    public string Persona { get; set; } = "personal";
    public string? SendFrom { get; set; }
    public string? EmailSpecificJson { get; set; }
    public string? LinkedInSpecificJson { get; set; }
    public string? RedditSpecificJson { get; set; }
    public string? DestinationUrl { get; set; }
    public string? ContextUrl { get; set; }
}
```

Keep `EmailSpecificDto` -- it's still needed for JSON parsing inside the email dispatcher.

### Step 2: Add `RedditSpecificDto` (next to `EmailSpecificDto`)

```csharp
private class RedditSpecificDto
{
    [System.Text.Json.Serialization.JsonPropertyName("parent_comment")]
    public string? ParentComment { get; set; }
}
```

### Step 3: Add `DispatchResult` enum

```csharp
private enum DispatchResult { Sent, Failed, Skipped }
```

### Step 4: Replace `GetApprovedEmails` with `GetApprovedItems` (lines 691-745)

Change the SQL query to return ALL approved non-hold items:

```sql
SELECT id, ticket_number, platform, type, content, persona, send_from,
       email_specific, linkedin_specific, reddit_specific,
       destination_url, context_url
FROM communications
WHERE status = 'approved'
AND (send_timing IS NULL OR send_timing != 'hold')
```

Return `List<QueuedItem>`. No JSON parsing here -- each platform dispatcher parses its own JSON.

### Step 5: Rewrite `SendAllAsync` (lines 635-689)

```
- Call GetApprovedItems() instead of GetApprovedEmails()
- Track 3 counters: sent, failed, skipped
- For each item: call DispatchItemAsync(item, dbPath) -> DispatchResult
- Progress message: "Dispatching 3/7: [linkedin] ticket #42"
- Final message: "Dispatch complete: 5 sent, 1 failed, 1 skipped"
```

### Step 6: Add `DispatchItemAsync` -- the platform router

```csharp
private async Task<DispatchResult> DispatchItemAsync(QueuedItem item, string dbPath)
{
    switch (item.Platform.ToLower())
    {
        case "email":
            return await DispatchEmailItemAsync(item, dbPath) ? DispatchResult.Sent : DispatchResult.Failed;
        case "linkedin":
            return await DispatchLinkedInItemAsync(item, dbPath) ? DispatchResult.Sent : DispatchResult.Failed;
        case "reddit":
            return await DispatchRedditItemAsync(item, dbPath) ? DispatchResult.Sent : DispatchResult.Failed;
        default:
            FileLog.Write($"[CommunicationManager.VM] DispatchItemAsync: platform '{item.Platform}' not supported, skipping ticket #{item.TicketNumber}");
            return DispatchResult.Skipped;
    }
}
```

### Step 7: Extract `RunToolAndMarkPostedAsync` shared helper

All 3 platform dispatchers share the same process-execution pattern. Extract it:

```csharp
private static async Task<bool> RunToolAndMarkPostedAsync(
    string toolPath, List<string> args, QueuedItem item, string dbPath, string toolName)
```

This method:
1. Creates `ProcessStartInfo` with `ArgumentList` (NOT string concatenation)
2. Starts the process
3. Reads stdout and stderr concurrently (to avoid deadlock)
4. Awaits process exit
5. On exit code 0: calls `MarkPosted(item.Id, dbPath)`, logs success, returns true
6. On failure: logs the error (stderr preferred over stdout), returns false

Copy the pattern from the existing `DispatchEmailAsync` lines 780-812.

### Step 8: Refactor `DispatchEmailAsync` -> `DispatchEmailItemAsync` (lines 747-813)

- Accept `QueuedItem` instead of `QueuedEmail`
- Parse `item.EmailSpecificJson` into `EmailSpecificDto` at the top of the method
- Return false if no email_specific data or no recipients
- Build args the same way as before
- Call `RunToolAndMarkPostedAsync` instead of inline process execution

### Step 9: Add `DispatchLinkedInItemAsync`

Handles two content types:

**Post** (`item.Type == "post"`):
```
cc-browser (LinkedIn) create "<content>" [--image <path>]
```
- Args: `"create"`, `item.Content`
- If media image exists: add `"--image"`, `imagePath`
- Call `ExtractFirstImageAsync(item.Id)` to get image from DB

**Comment** (`item.Type == "comment"`):
```
cc-browser (LinkedIn) comment <url> --text "<text>"
```
- Args: `"comment"`, `targetUrl`, `"--text"`, `item.Content`
- URL from `item.DestinationUrl ?? item.ContextUrl`
- Return false if no URL

Unsupported types (e.g. "reply" -- cc-browser (LinkedIn) doesn't have a reply command): log and return false.

### Step 10: Add `DispatchRedditItemAsync`

Handles two content types:

**Comment** (`item.Type == "comment"`):
```
cc-reddit comment <post-url> --text "<text>"
```
- Args: `"comment"`, `postUrl`, `"--text"`, `item.Content`
- URL from `item.DestinationUrl ?? item.ContextUrl`

**Reply** (`item.Type == "reply"`):
```
cc-reddit reply <comment-url> --text "<text>"
```
- Args: `"reply"`, `replyUrl`, `"--text"`, `item.Content`
- URL from `item.DestinationUrl`
- Fallback: parse `item.RedditSpecificJson` -> `RedditSpecificDto.ParentComment`

Return false if no URL for either type.

### Step 11: Add `ExtractFirstImageAsync` helper

```csharp
private async Task<string?> ExtractFirstImageAsync(string communicationId)
```

1. Query `media` table: `WHERE communication_id = @commId AND (mime_type LIKE 'image/%' OR type = 'image') LIMIT 1`
2. Read BLOB data
3. Write to temp file: `Path.Combine(Path.GetTempPath(), "comm_manager_media", "{mediaId}_{filename}")`
4. Return temp path (or null if no image)
5. Wrap DB work in `Task.Run()` since it's synchronous I/O

### Step 12: Fix `PostToLinkedInAsync` unsafe string concatenation (lines 540-629)

Line 587 uses:
```csharp
Arguments = string.Join(" ", args),  // UNSAFE
```

Change to use `ArgumentList` like the rest of the dispatch methods. Remove the manual quote-escaping on lines 574-575 and 580 since `ArgumentList` handles quoting automatically.

---

## Tool Commands Reference

| Platform | Type | CLI Command |
|----------|------|-------------|
| Email (Gmail) | email | `cc-gmail send -t <to> -s <subject> -b <body> --html [--cc <cc>] [--bcc <bcc>] [--attach <path>]` |
| Email (Outlook) | email | `cc-outlook send -t <to> -s <subject> -b <body> --html [--cc <cc>] [--bcc <bcc>] [-a <path>]` |
| LinkedIn | post | `cc-browser (LinkedIn) create "<content>" [--image <path>]` |
| LinkedIn | comment | `cc-browser (LinkedIn) comment <post-url> --text "<text>"` |
| Reddit | comment | `cc-reddit comment <post-url> --text "<text>"` |
| Reddit | reply | `cc-reddit reply <comment-url> --text "<text>"` |

Tool binaries are at `CcStorage.Bin()` -> `%LOCALAPPDATA%\cc-director\bin\`.

---

## Platform-Specific JSON Columns

| Column | Key Fields |
|--------|------------|
| `email_specific` | `to` (list), `cc` (list), `bcc` (list), `subject`, `attachments` (list) |
| `linkedin_specific` | `visibility` (public/connections) -- cc-browser (LinkedIn) doesn't support this flag yet |
| `reddit_specific` | `subreddit`, `subreddit_url`, `parent_comment`, `title`, `flair` |

---

## Email Routing Logic

```
send_from (or persona if null) -> checked against _gmailAccounts set {"personal"}
If match or contains "@gmail.com" -> cc-gmail
Otherwise -> cc-outlook
Attachment flag: cc-gmail uses --attach, cc-outlook uses -a
```

---

## Rules and Constraints

- **FileLog.Write** for all public methods: entry, exit, and errors. Format: `[CommunicationManager.VM] MethodName: context`
- **Try-catch at entry points only** (SendAllAsync). Helper methods throw, callers catch.
- **No fallback programming** -- if a tool fails, log clearly and return false. Don't try alternatives.
- **ArgumentList, not Arguments** -- use `psi.ArgumentList.Add(arg)` for safe argument passing.
- **Concurrent stdout/stderr reads** -- always read both streams before `WaitForExitAsync()` to avoid deadlock.
- **UI thread safety** -- `IsSending` and `StatusMessage` are fine from async methods (they're on the captured sync context). `ObservableCollection` changes must use `Dispatcher.BeginInvoke`.
- **No null-forgiving operators** (`!`) -- use explicit null checks.
- **No Debug.WriteLine** -- use `FileLog.Write`.
- **Hold items** must NOT be dispatched. The query filters them out.

---

## What NOT To Change

- `CommunicationDispatcher.cs` -- auto-dispatch is disabled (timer commented out line 44). Multi-platform expansion there is a separate follow-up task.
- `CommunicationManagerView.xaml` -- SEND ALL button text is already correct.
- Database schema -- already supports all platforms.
- `MarkPosted` method -- already platform-agnostic, works as-is.

---

## Verification

1. Build the solution: `dotnet build src/CcDirector.CommunicationManager`
2. Add test items:
   ```bash
   cc-comm-queue add email email --to test@example.com --subject "Test Email" --body "<p>Hello</p>" --persona personal
   cc-comm-queue add linkedin post --content "Test LinkedIn post" --persona personal
   ```
3. Open Communication Manager, approve both items
4. Click SEND ALL
5. Verify status bar shows: `"Dispatching 1/2: [email] ticket #X"` then `"Dispatching 2/2: [linkedin] ticket #Y"`
6. Verify final message: `"Dispatch complete: 2 sent"` (or with failed/skipped counts)
7. Check items moved to Posted tab
8. Check `FileLog` output for proper entry/exit/error logging on every method
9. If any unsupported platform items exist, verify they show as "skipped" not "failed"
