// Raw-view harness.
//
// Reads a real session's raw.jsonl (timestamped, base64-encoded PTY chunks
// captured by SessionLogManager), replays the bytes through AnsiParser into
// a fresh grid + scrollback, then emits a self-contained HTML file that
// shows the Raw terminal exactly the way the session-view does.
//
// Usage:
//   cc-raw-view-harness <session-log-dir> [-o output.html] [--cols N] [--rows N]
//
// The output is STATIC -- no polling, no fetch. That's the point: a known
// input produces a known visual artifact you can open at any viewport size,
// iterate on CSS in this Program.cs's template, re-run, and compare. No
// Director rebuild required between iterations.
//
// Session log dirs live under %LOCALAPPDATA%\cc-director\session-logs\<sid>\.

using System.Text;
using System.Text.Json;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

const int DefaultCols = 220;
const int DefaultRows = 40;
const int MaxScrollback = 5000;

string? sessionDir = null;
string outputPath = "raw-view.html";
int cols = DefaultCols;
int rows = DefaultRows;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length) { outputPath = args[++i]; }
    else if (args[i] == "--cols" && i + 1 < args.Length) { cols = int.Parse(args[++i]); }
    else if (args[i] == "--rows" && i + 1 < args.Length) { rows = int.Parse(args[++i]); }
    else if (!args[i].StartsWith("-")) { sessionDir = args[i]; }
}

if (sessionDir is null)
{
    Console.Error.WriteLine("Usage: cc-raw-view-harness <session-log-dir> [-o output.html] [--cols N] [--rows N]");
    return 1;
}

var rawJsonl = Path.Combine(sessionDir, "raw.jsonl");
if (!File.Exists(rawJsonl))
{
    Console.Error.WriteLine($"Not found: {rawJsonl}");
    return 1;
}

var cells = new TerminalCell[cols, rows];
var scrollback = new List<TerminalCell[]>();
var parser = new AnsiParser(cells, cols, rows, scrollback, MaxScrollback);

long totalBytes = 0;
int chunks = 0;
foreach (var line in File.ReadLines(rawJsonl))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    using var doc = JsonDocument.Parse(line);
    if (!doc.RootElement.TryGetProperty("b64", out var b64Prop)) continue;
    var b64 = b64Prop.GetString();
    if (string.IsNullOrEmpty(b64)) continue;
    var bytes = Convert.FromBase64String(b64);
    parser.Parse(bytes);
    totalBytes += bytes.Length;
    chunks++;
}

var (scrollbackHtml, gridHtml) = AnsiToHtmlConverter.ConvertToHtmlSplit(scrollback, cells, cols, rows);

Console.WriteLine($"Replayed {chunks:N0} chunks ({totalBytes:N0} bytes) from {Path.GetFileName(sessionDir)}");
Console.WriteLine($"Grid: {cols} x {rows} | scrollback rows: {scrollback.Count}");
Console.WriteLine($"scrollbackHtml: {scrollbackHtml.Length:N0} chars | gridHtml: {gridHtml.Length:N0} chars");

var page = BuildHtml(scrollbackHtml, gridHtml, scrollback.Count, totalBytes, cols, rows, Path.GetFileName(sessionDir));
File.WriteAllText(outputPath, page);
Console.WriteLine($"Wrote {outputPath} ({new FileInfo(outputPath).Length:N0} bytes)");
return 0;


static string BuildHtml(string scrollbackHtml, string gridHtml, int scrollbackCount, long totalBytes, int cols, int rows, string sessionLabel)
{
    var sb = new StringBuilder();
    sb.Append("""
<!doctype html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>Raw view harness</title>
<style>
  :root {
    --bg: #1e1e1e;
    --panel: #252526;
    --border: #2d2d30;
    --text: #cccccc;
    --muted: #888888;
    --accent: #0e639c;
  }
  * { box-sizing: border-box; }
  html, body {
    margin: 0; padding: 0;
    background: var(--bg);
    color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    min-height: 100vh;
  }
  header {
    padding: 8px 14px;
    background: var(--panel);
    border-bottom: 1px solid var(--border);
    color: var(--muted);
    font-size: 11px;
    padding-top: max(8px, env(safe-area-inset-top));
  }
  header strong { color: var(--text); }

  /* ===== RAW VIEW =====
     Iteration target. Same structure the session-view should adopt:
       #rawView          -- container, has its own height + internal scroll
       #rawScrollback    -- history, scrolls past
       #rawLiveGrid      -- bottom of the scroll, what 'top of terminal feels like'
     No sticky. Fixed-height container with overflow-y so the scrollback can
     truly leave the viewport when the user scrolls down to the live grid. */
  #rawView {
    /* The container reserves exactly the space between header and (simulated)
       input bar. Internal vertical scroll = no page-level scroll. This is how
       the desktop terminal control behaves: scrollback is reachable only by
       scrolling up inside the terminal area.

       IMPORTANT: zero vertical padding. The visual padding lives inside the
       children so liveGrid's `min-height: 100%` actually equals rawView's
       clientHeight (rather than clientHeight - 2*padding, which would leave
       a sliver of scrollback peeking through at the top when scrolled to
       the bottom). Horizontal padding stays on rawView for the scrollbar. */
    position: fixed;
    top: 38px; /* header height */
    bottom: calc(64px + env(safe-area-inset-bottom));
    left: 0; right: 0;
    padding: 0 14px;
    font-family: "Cascadia Code", Consolas, monospace;
    font-size: 12.5px;
    line-height: 1.2;
    color: #D4D4D8;
    background: #1e1e1e;
    overflow-x: auto;
    overflow-y: auto;
  }
  #rawView .line { white-space: pre; min-height: 1.2em; margin: 0; padding: 0; }
  #rawView .rv  { filter: invert(1); }

  #rawScrollback { padding-top: 10px; }

  /* The live grid must FILL the viewport when the user is scrolled to the
     bottom of rawView. Otherwise the tail of scrollback peeks in above it
     and the user sees what looks like a duplicate status bar (the OLD
     status bar that scrolled into scrollback) right above the current one.

     min-height: 100% now equals rawView's full clientHeight (because rawView
     has no vertical padding). Flex justify-end pushes the 40 grid rows to
     the bottom of liveGrid; everything above the rows is blank dark space
     that pushes scrollback fully off the top of the visible area. */
  #rawLiveGrid {
    min-height: 100%;
    padding-bottom: 10px;
    display: flex;
    flex-direction: column;
    justify-content: flex-end;
  }

  /* Simulated input bar so the spacing/proportions match the real session-view. */
  .send-bar {
    position: fixed;
    bottom: 0; left: 0; right: 0;
    background: var(--panel);
    border-top: 1px solid var(--border);
    padding: 10px 12px;
    padding-bottom: calc(10px + env(safe-area-inset-bottom));
    display: flex; gap: 8px;
    z-index: 20;
  }
  .send-bar input {
    flex: 1; background: var(--bg); color: var(--text); border: 1px solid var(--border);
    border-radius: 8px; padding: 12px 14px; font-size: 16px; min-height: 44px;
    font-family: inherit;
  }
  .send-bar button {
    background: var(--accent); color: white; border: 0;
    padding: 0 18px; border-radius: 8px; font-weight: 600; font-size: 15px;
    min-height: 44px; min-width: 64px;
  }
</style>
</head>
<body>
""");
    sb.Append("<header>STATIC FIXTURE | session ").Append(sessionLabel)
      .Append(" | scrollback <strong>").Append(scrollbackCount).Append("</strong> rows")
      .Append(" | grid <strong>").Append(cols).Append('x').Append(rows).Append("</strong>")
      .Append(" | replayed <strong>").Append(totalBytes.ToString("N0")).Append("</strong> bytes")
      .Append("</header>\n");
    sb.Append("<div id=\"rawView\">\n  <div id=\"rawScrollback\">").Append(scrollbackHtml).Append("</div>\n");
    sb.Append("  <div id=\"rawLiveGrid\">").Append(gridHtml).Append("</div>\n</div>\n");
    sb.Append("""
<form class="send-bar" onsubmit="event.preventDefault();return false">
  <input type="text" placeholder="(simulated input bar - not connected)">
  <button type="button">Send</button>
</form>
<script>
  // On load: scroll the rawView to its bottom so the live grid fills the
  // visible area, exactly like opening a real terminal. The user then scrolls
  // UP inside rawView to see scrollback. Scrolling down has nothing left
  // because the live grid IS the bottom.
  const rawView = document.getElementById('rawView');
  rawView.scrollTop = rawView.scrollHeight;
</script>
</body></html>
""");
    return sb.ToString();
}
