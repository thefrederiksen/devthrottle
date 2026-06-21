# Handover: Communication Manager -- Multi-Channel Dispatch

## What Has Been Done

### 1. Scheduling UI (Complete)
The Communication Manager WPF app now has full scheduling support:
- **ScheduleDialog** (`Views/ScheduleDialog.xaml`) -- modal dialog with ASAP/scheduled/hold/presets when approving items
- **TimelineView** (`Views/TimelineView.xaml`) -- day-grouped timeline visualization on the Approved tab
- **Date filter bar** -- Today/Tomorrow/This Week/All Upcoming filters on the Approved tab
- **View toggle** -- List vs Timeline view on the Approved tab
- **Schedule display** -- color-coded timing badges in list items and detail panel
- **Reschedule button** -- re-opens schedule dialog for approved items

### 2. Manual Send Button (Temporary -- Needs Expansion)
A big red **SEND ALL** button was added to the header bar of the Communication Manager. Currently it:
- Queries the SQLite database for `status='approved' AND platform='email' AND send_timing != 'hold'`
- Dispatches each email via `cc-gmail` or `cc-outlook` based on `send_from` account
- Marks items as `posted` with `posted_by = 'cc-director-manual'`
- Shows per-item progress in the status bar

**The auto-dispatch timer in CommunicationDispatcher has been disabled.** The timer line is commented out in `CommunicationDispatcher.cs:44` with a TODO to re-enable later.

### 3. Data Model (Complete)
The database schema and models fully support all channels:
- `send_timing` field: immediate, scheduled, asap, hold
- `scheduled_for` field: ISO datetime for scheduled sends
- `send_from` field: account identifier (mindzie, personal, consulting)
- Platform-specific JSON columns for each channel (email_specific, linkedin_specific, reddit_specific, etc.)

---

## What Needs To Be Done

### Goal
Expand the SEND ALL button to dispatch **all platforms**, not just email. Each platform uses its own cc-tool for sending. The button should iterate through all approved items (excluding hold), determine the platform, and call the appropriate tool.

### Architecture: One Tool Per Channel

| Platform | Tool | Send Command | Status |
|----------|------|-------------|--------|
| Email (Gmail) | `cc-gmail` | `cc-gmail send -t <to> -s <subject> -b <body> --html` | WORKING |
| Email (Outlook) | `cc-outlook` | `cc-outlook send -t <to> -s <subject> -b <body> --html` | WORKING |
| LinkedIn | `cc-browser (LinkedIn connection)` | `cc-browser (LinkedIn connection) create "<content>" [--image <path>]` | WORKING |
| Reddit | `cc-reddit` | `cc-reddit comment <post_url> --text "<text>"` | PARTIAL (comment/reply only, no new posts) |
| Twitter/X | `cc-twitter` | Does not exist yet | NOT BUILT |
| Facebook | `cc-facebook` | Does not exist yet | NOT BUILT |
| WhatsApp | `cc-whatsapp` | Does not exist yet | NOT BUILT |
| YouTube | `cc-youtube` | Does not exist yet | NOT BUILT |
| Blog | `cc-blog` | Does not exist yet | NOT BUILT |

### Priority Order
1. **Email** -- already working in SendAll
2. **LinkedIn** -- tool exists (`cc-browser (LinkedIn connection) create`), just needs wiring into SendAll
3. **Reddit** -- tool exists for comments/replies, needs wiring
4. Twitter, Facebook, WhatsApp, YouTube, Blog -- future tools

---

## Key Files

### Communication Manager (WPF App)
| File | Purpose |
|------|---------|
| `src/CcDirector.CommunicationManager/ViewModels/MainViewModel.cs` | **SendAllAsync command lives here** -- this is where dispatch logic needs to be expanded |
| `src/CcDirector.CommunicationManager/CommunicationManagerView.xaml` | SEND ALL button in header bar |
| `src/CcDirector.CommunicationManager/Models/ContentItem.cs` | Content model with platform-specific data |
| `src/CcDirector.CommunicationManager/Services/ContentService.cs` | DB operations (approve, reject, mark posted) |
| `src/CcDirector.CommunicationManager/Services/DatabaseService.cs` | Raw SQLite queries |

### Engine Dispatcher (Disabled)
| File | Purpose |
|------|---------|
| `src/CcDirector.Engine/Dispatcher/CommunicationDispatcher.cs` | **Auto-dispatch timer (DISABLED)** -- has email-only dispatch logic. Re-enable when manual testing is done |
| `src/CcDirector.Engine/EngineHost.cs` | Starts/stops the dispatcher |
| `src/CcDirector.Engine/EngineOptions.cs` | Config: DB path, tool paths, Gmail account list, poll interval |

### Queue System
| File | Purpose |
|------|---------|
| `tools/cc-comm-queue/src/schema.py` | **Platform enum, SendTiming enum, platform-specific models** (LinkedInSpecific, EmailSpecific, RedditSpecific, etc.) |
| `tools/cc-comm-queue/src/database.py` | SQLite schema and queries |
| `tools/cc-comm-queue/src/cli.py` | CLI for adding items to queue |

### Channel Tools
| File | Purpose |
|------|---------|
| `tools/cc-gmail/src/cli.py` | Gmail CLI -- `send` command |
| `tools/cc-gmail/src/gmail_api.py` | Gmail API -- `send_message()` |
| `tools/cc-outlook/src/cli.py` | Outlook CLI -- `send` command |
| `tools/cc-outlook/src/outlook_api.py` | Outlook API -- `send_message()` |
| `tools/cc-browser (LinkedIn connection)/src/cli.py` | LinkedIn CLI -- `create` command |
| `tools/cc-reddit/src/cli.py` | Reddit CLI -- `comment`, `reply` commands |

---

## How SendAll Currently Works

In `MainViewModel.cs`, the `SendAllAsync` method:

1. Reads `CcStorage.CommQueueDb()` to get the database path
2. Calls `GetApprovedEmails()` which queries:
   ```sql
   SELECT id, ticket_number, content, email_specific, persona, send_from
   FROM communications
   WHERE status = 'approved' AND platform = 'email'
   AND (send_timing IS NULL OR send_timing != 'hold')
   ```
3. For each email, calls `DispatchEmailAsync()` which:
   - Determines cc-gmail vs cc-outlook based on `send_from`
   - Builds CLI args: `send -t <to> -s <subject> -b <body> --html [--cc] [--bcc] [-a]`
   - Runs the tool process, reads stdout/stderr
   - On success: calls `MarkPosted(id, dbPath)` to set `status='posted'`
4. Updates status bar with progress and final count

---

## How To Expand SendAll For All Platforms

### Step 1: Change the DB query to return ALL approved items (not just email)

Replace the current `GetApprovedEmails` with a general `GetApprovedItems` that queries:
```sql
SELECT id, ticket_number, platform, type, content, persona, send_from,
       email_specific, linkedin_specific, reddit_specific
FROM communications
WHERE status = 'approved'
AND (send_timing IS NULL OR send_timing != 'hold')
```

### Step 2: Dispatch by platform

Create a dispatcher method per platform:

**Email** (already done):
```
cc-gmail send -t <to> -s <subject> -b <body> --html [--cc <cc>] [--bcc <bcc>] [--attach <path>]
cc-outlook send -t <to> -s <subject> -b <body> --html [--cc <cc>] [--bcc <bcc>] [-a <path>]
```

**LinkedIn**:
```
cc-browser (LinkedIn connection) create "<content>" [--image <path>]
```
- Content comes from the `content` field
- Image comes from the media table (extract via `ContentService.ExtractMediaToTemp()`)
- The `linkedin_specific` JSON has `visibility` (public/connections) -- cc-browser (LinkedIn connection) doesn't support this flag yet, may need adding

**Reddit**:
```
cc-reddit comment <post_url> --text "<content>"
cc-reddit reply <comment_url> --text "<content>"
```
- For comments: `reddit_specific.subreddit_url` or `destination_url` is the post URL
- For replies: `reddit_specific.parent_comment` is the comment URL
- Content type determines comment vs reply

### Step 3: Handle platform-specific data

Each platform stores its dispatch details in a JSON column:

| Platform | JSON Column | Key Fields |
|----------|-------------|------------|
| Email | `email_specific` | to, cc, bcc, subject, attachments |
| LinkedIn | `linkedin_specific` | visibility |
| Reddit | `reddit_specific` | subreddit, subreddit_url, parent_comment |
| Twitter | `twitter_specific` | reply_to, quote_tweet_url, is_thread |
| Facebook | `facebook_specific` | page_id, audience |
| WhatsApp | `whatsapp_specific` | phone_number, contact_name |
| YouTube | `youtube_specific` | title, description, tags, privacy_status |

### Step 4: Update MarkPosted

The current `MarkPosted` already works for all platforms. Optionally set `posted_url` if the tool returns a URL of the posted content.

---

## Database Location

```
%LOCALAPPDATA%\cc-director\data\comm-queue\communications.db
```

Accessed via `CcStorage.CommQueueDb()` in C# code.

---

## Testing

The user's workflow for testing:
1. Run one instance of cc-director with approved items in the queue
2. Run a second instance of cc-director
3. Click the SEND ALL button in the second instance
4. Verify items are dispatched and marked as posted
5. Refresh the first instance to see updated statuses

To add test items to the queue:
```bash
cc-comm-queue add email email --to test@example.com --subject "Test" --body "Hello" --persona personal
cc-comm-queue add linkedin post --content "Test LinkedIn post" --persona personal
```

---

## Rules and Constraints

- **LinkedIn**: Use cc-browser with the LinkedIn connection and navigation skill (has human-like delays)
- **Reddit**: Use cc-reddit (has human-like delays built in)
- **Email**: Must go through approval workflow. Direct `cc-gmail send` / `cc-outlook send` is forbidden except through the dispatcher
- **Hold items**: Must NOT be auto-sent. Query filters out `send_timing = 'hold'`
- **No fallback programming**: If a tool fails, report the error clearly. Don't try alternative approaches
- **FileLog.Write**: All public methods must log entry and exit
- **UI thread safety**: Don't modify ObservableCollection from background threads
- **Coding style**: See `docs/CodingStyle.md` -- no null-forgiving operators, no Debug.WriteLine (use FileLog.Write)

---

## After Manual Testing Is Complete

1. Re-enable the auto-dispatch timer in `CommunicationDispatcher.cs:44` (uncomment the timer line)
2. Update `CommunicationDispatcher` to handle all platforms (same pattern as SendAll)
3. Add `send_timing`/`scheduled_for` checks to the dispatcher query
4. Remove or keep the SEND ALL button (could be useful as manual override)
