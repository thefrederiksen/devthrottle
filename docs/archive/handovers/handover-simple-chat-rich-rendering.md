# Handover: Simple Chat Rich Content Rendering

## Status: Implemented -- Ready for Manual Testing

**Date:** 2026-03-02
**Screenshot (before):** `docs/simple-chat-plain-text-table.png`

---

## What Was Done

### Problem

The Simple Chat tab rendered all assistant messages as plain `TextBlock` elements. When Claude's response contained tables, lists, or headers, they displayed as flat unformatted text with no column alignment, no bullet formatting, and no visual hierarchy.

### Solution: FlowDocument Rendering via Markdig

Instead of the WebView2 approach (heavy -- per-bubble Chromium instances, JavaScript height hacks), we extended the existing `MarkdownFlowDocumentRenderer` which already converts markdown to WPF FlowDocument. The key insight: Markdig 0.38.0 and `MarkdownFlowDocumentRenderer` were already in the project, handling headings, paragraphs, code blocks, lists, and quotes. Only **table support** was missing.

The pipeline is now:

```
Terminal output (raw)
    |
    v
SimpleChatSummarizer.SummarizeCompletionAsync()
    -- Haiku now outputs MARKDOWN (tables, headers, lists, code)
    |
    v
ChatMessage(Assistant, markdownText)
    |
    v
ChatMessageViewModel.RenderedDocument
    -- MarkdownFlowDocumentRenderer.Render(text, embedded: true)
    -- Returns FlowDocument with transparent bg, zero padding
    |
    v
ChatMessageTemplateSelector picks RichTemplate
    -- FlowDocumentScrollViewer displays the FlowDocument
```

### Files Changed

| File | Change |
|------|--------|
| `src/CcDirector.Wpf/Helpers/MarkdownFlowDocumentRenderer.cs` | Added table rendering + embedded mode |
| `src/CcDirector.Core/Claude/SimpleChatSummarizer.cs` | Changed completion prompt to request markdown |
| `src/CcDirector.Wpf/Controls/ChatMessageViewModel.cs` | Added RenderedDocument + IsRichContent |
| `src/CcDirector.Wpf/Controls/ChatMessageTemplateSelector.cs` | NEW - DataTemplateSelector |
| `src/CcDirector.Wpf/Controls/SimpleChatView.xaml` | Two templates + selector |

### Detailed Changes

**1. MarkdownFlowDocumentRenderer.cs**
- Added `using Markdig.Extensions.Tables` with type aliases (`MdTableRow`, `MdTableCell`, `WpfTableRow`, `WpfTableCell`) to disambiguate WPF and Markdig table types
- Added frozen brushes: `TableHeaderForeground` (#569CD6, VS Code blue), `TableAltRowBackground` (#252526)
- Added `RenderTable()` method: WPF Table with auto-sized columns, blue semibold header row, alternating row backgrounds, 0.5px borders, 8px cell padding
- Added switch case: `Markdig.Extensions.Tables.Table table => RenderTable(table)`
- Added overload `Render(string markdown, bool embedded)`: when `embedded=true`, transparent background and zero PagePadding (for chat bubbles). Original `Render(string)` delegates to `Render(markdown, embedded: false)`.

**2. SimpleChatSummarizer.cs**
- Changed one line in `SummarizeCompletionAsync` system prompt:
  - Old: `"Use plain text with newlines and spacing for structure (no markdown syntax)"`
  - New: `"Format using markdown: pipe tables for tabular data, # for headers, - for bullet lists, backticks for code"`
- Progress prompt (`SummarizeProgressAsync`) is unchanged -- status messages stay plain text

**3. ChatMessageViewModel.cs**
- Added `using System.Windows.Documents` and `using CcDirector.Wpf.Helpers`
- Added `private readonly FlowDocument? _renderedDocument` field
- In constructor: for Assistant messages, renders markdown via `MarkdownFlowDocumentRenderer.Render(text, embedded: true)`. Null for all other message types.
- Added `FlowDocument? RenderedDocument` property (exposes the cached field)
- Added `bool IsRichContent => _renderedDocument != null`

**4. ChatMessageTemplateSelector.cs (NEW)**
- `DataTemplateSelector` subclass with `PlainTemplate` and `RichTemplate` properties
- Returns `RichTemplate` when `ChatMessageViewModel.IsRichContent` is true, `PlainTemplate` otherwise

**5. SimpleChatView.xaml**
- Moved existing inline DataTemplate to `UserControl.Resources` as `PlainChatTemplate` (unchanged structure -- TextBlock in Border)
- Created `RichChatTemplate`: same bubble structure but replaces TextBlock with `FlowDocumentScrollViewer` (Document bound to RenderedDocument, scrollbars disabled, toolbar hidden, Focusable=False, transparent background). MaxWidth widened to 720 (from 600) for table readability.
- Added `ChatMessageTemplateSelector` resource wiring both templates
- Replaced `ItemsControl.ItemTemplate` with `ItemsControl.ItemTemplateSelector`

### Build & Test Results

- **Build:** 0 warnings, 0 errors
- **Tests:** All 12 SimpleChatSummarizer tests pass. 659/663 total pass (4 pre-existing failures in unrelated TerminalVerificationIntegrationTests)

---

## What Still Needs to Be Done

### 1. Manual Testing (Priority: HIGH)

Launch cc-director, open Simple Chat tab, and verify:
- Ask Claude a question that produces a table (e.g., "list the tools in this project as a table")
- Assistant message renders with formatted table (columns, header row, blue headers, alternating backgrounds)
- User messages still render as plain blue bubbles
- Status messages still render as italic plain text
- Timestamps display correctly for both templates
- Outer ScrollViewer still scrolls the full chat
- Short plain-text assistant responses render cleanly (no weird empty space or oversized bubbles)
- Multiple messages in sequence look correct (mixed plain + rich)

### 2. FlowDocumentScrollViewer Sizing (Priority: HIGH -- likely issue)

`FlowDocumentScrollViewer` may not auto-size its height to content when nested inside an ItemsControl. If assistant bubbles show with excessive empty space or require internal scrolling, we may need:
- Set `MinHeight`/`MaxHeight` constraints
- Or switch to `RichTextBox` (IsReadOnly=True, IsDocumentEnabled=True) which sizes better in stack layouts
- Or measure the FlowDocument and set explicit height

This is the most likely thing that will need a fix after manual testing.

### 3. Table Column Widths (Priority: MEDIUM)

WPF Table columns are currently auto-sized (no explicit width). If tables have very wide content, columns may overflow the 720px MaxWidth or look cramped. May need:
- `TableColumn.Width = new GridLength(1, GridUnitType.Star)` for equal distribution
- Or measure content and assign proportional widths

### 4. Inline Code Rendering in Tables (Priority: LOW)

Backtick code inside table cells should render with monospace font and dark background. This already works via `AddInlines` -> `CodeInline` handling, but hasn't been tested inside table cells specifically.

### 5. Horizontal Rule in Tables (Priority: LOW)

Markdig table separator rows (the `|---|---|` lines) are handled by the parser, not rendered as nodes. Should be fine, but verify with manual testing.

### 6. Edge Cases (Priority: LOW)

- Very long tables (20+ rows) -- should scroll with the outer chat ScrollViewer, not overflow
- Tables with many columns (5+) -- may need horizontal scroll or smaller font
- Empty assistant messages -- RenderedDocument will be an empty FlowDocument (verify it doesn't show as a weird empty box)
- Messages with only a single line of text -- should look the same as the old plain TextBlock rendering

### 7. Future: Teams/HTML Integration

The current approach uses WPF FlowDocument, which is not directly reusable for Teams messages or email. If we later need HTML output for other channels, we could:
- Add a `MarkdownToHtmlRenderer` in Core (Markdig has built-in HTML rendering)
- Use `MarkdownFlowDocumentRenderer` for WPF and HTML renderer for channels
- The markdown source text is preserved in `ChatMessage.Text`, so we can render it differently per channel

This is not blocking -- the Simple Chat view works independently.

---

## Architecture Notes

**Why FlowDocument over WebView2:**
- WebView2 creates a Chromium process per instance -- heavy for a chat view with many bubbles
- WebView2 doesn't auto-size height to content (needs JavaScript postMessage hacks)
- FlowDocument is native WPF, renders instantly, sizes naturally in layout
- The existing `MarkdownFlowDocumentRenderer` already handled 90% of the markdown elements
- Only table support was missing (now added)

**Why markdown output from Haiku:**
- Markdig parses markdown natively -- no custom parsing heuristics needed
- Pipe tables (`| col1 | col2 |`) are unambiguous vs. detecting column alignment from whitespace
- Markdown is a well-known format -- Haiku produces it reliably
- The same markdown can be rendered differently for different targets (FlowDocument, HTML, plain text)

**Thread safety note:**
- `FlowDocument` is a `DispatcherObject` -- must be created on the UI thread
- `ChatMessageViewModel` instances are created via `Dispatcher.BeginInvoke` in the code-behind (verified in `SimpleChatView.xaml.cs`)
- Rendering happens in the constructor, which is acceptable for Haiku output sizes (< 1500 chars)
