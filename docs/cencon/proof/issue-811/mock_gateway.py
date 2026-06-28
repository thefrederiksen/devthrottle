"""
Mock Gateway for issue #811 Chat-mode proof.

Serves the REAL built mobile PWA bundle (mobile/dist) at /m, exactly as the Gateway does, and
faithfully mocks the session endpoints the Chat view talks to: GET /sessions, GET
/sessions/{sid}/history (seeded with Text + Thinking + ToolUse + ToolResult parts so AC2/AC3 can be
proven), POST /prompt|/escape|/interrupt, the /wingman/utterance/* dictation flow, and the
WS /sessions/{sid}/stream raw mirror. Every request is logged (method, path, Authorization header,
body) to requests.jsonl so the AC captures (Send body, control-key bodies, bearer in both auth
modes) come straight from the wire.

Run: python mock_gateway.py <dist_dir> <port> <auth on|off> <log_path>
"""
import json
import sys
import asyncio
from pathlib import Path
from aiohttp import web, WSMsgType

DIST = Path(sys.argv[1])
PORT = int(sys.argv[2])
AUTH = sys.argv[3] == "on"
LOG = Path(sys.argv[4])
TEST_TOKEN = "test-gateway-token-811"

SID = "11111111-1111-1111-1111-111111111111"

# A raw transcript line carrying an ANSI red code (proves CleanForReading strips ANSI), a
# <system-reminder> machinery block (proves it is dropped), a literal <script> (proves HTML is
# DISABLED / inert), a </task-id> placeholder (proves placeholder-tag wrapping), Markdown
# (heading/bold/list/fenced code), and a URL + absolute path (proves link detection).
ASSISTANT_TEXT = (
    "## Summary\n\n"
    "Done **refactoring** the detector. Steps:\n\n"
    "- parsed the input\n"
    "- ran the \x1b[31mtests\x1b[0m (ANSI should be stripped)\n\n"
    "```cs\nvar x = LinkDetector.Find(line);\n```\n\n"
    "Literal HTML must render inert: <script>alert('xss')</script>\n\n"
    "<system-reminder>internal machinery a person should never see</system-reminder>\n\n"
    "Placeholder token: pass </task-id> through as a chip.\n\n"
    "Docs: https://github.com/thefrederiksen/devthrottle and file D:\\repo\\src\\LinkDetector.cs"
)


def base_history():
    return {
        "sessionId": SID,
        "directorId": "dir-test",
        "agent": "ClaudeCode",
        "isSupported": True,
        "isRawText": False,
        "historyState": "Idle",
        "status": "ok",
        "error": None,
        "messages": [
            {
                "role": "User",
                "parts": [
                    {"kind": "Text",
                     "text": "Please refactor LinkDetector and verify https://example.com/docs works.",
                     "toolName": None, "toolId": None},
                ],
                "timestamp": None,
            },
            {
                "role": "Assistant",
                "parts": [
                    {"kind": "Text", "text": ASSISTANT_TEXT, "toolName": None, "toolId": None},
                    {"kind": "Thinking",
                     "text": "I should consider the quoted-path and trailing-punctuation edge cases.",
                     "toolName": None, "toolId": None},
                    {"kind": "ToolUse",
                     "text": "{\"file_path\":\"D:\\\\repo\\\\src\\\\LinkDetector.cs\"}",
                     "toolName": "Read", "toolId": "tool_1"},
                    {"kind": "ToolResult",
                     "text": "1  using System;\n2  namespace X { }",
                     "toolName": None, "toolId": "tool_1"},
                ],
                "timestamp": None,
            },
            {
                # A pure tool-result User message -> the gold "Tool result" bubble (hidden by default).
                "role": "User",
                "parts": [
                    {"kind": "ToolResult",
                     "text": "exit code 0\nbuild succeeded, 0 warnings",
                     "toolName": None, "toolId": "tool_1"},
                ],
                "timestamp": None,
            },
        ],
    }


# Mutable history so a Send appends a new user turn the 2.5s poll picks up (AC5/AC6).
HISTORY = base_history()


def log_request(method, path, headers, body):
    rec = {
        "method": method,
        "path": path,
        "authorization": headers.get("Authorization", None),
        "body": body,
    }
    with LOG.open("a", encoding="utf-8") as f:
        f.write(json.dumps(rec) + "\n")


async def maybe_auth(request):
    """In auth-on mode, require the Bearer the injected page must attach (proves the client sends it)."""
    if not AUTH:
        return None
    auth = request.headers.get("Authorization", "")
    if auth != f"Bearer {TEST_TOKEN}":
        return web.json_response({"error": "missing bearer"}, status=401)
    return None


async def get_sessions(request):
    log_request("GET", "/sessions", request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response([
        {"sessionId": SID, "name": "Proof Session 811", "directorId": "dir-test",
         "repoPath": "D:\\repo", "status": "Idle"}
    ])


async def get_history(request):
    log_request("GET", request.path, request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response(HISTORY)


async def post_prompt(request):
    body = await request.text()
    log_request("POST", request.path, request.headers, body)
    denied = await maybe_auth(request)
    if denied:
        return denied
    try:
        data = json.loads(body)
    except Exception:
        data = {}
    text = data.get("text", "")
    append_enter = data.get("appendEnter", False)
    # A real typed Send (appendEnter true) becomes a new visible user turn; raw key bytes do not.
    if append_enter and text.strip():
        HISTORY["messages"].append({
            "role": "User",
            "parts": [{"kind": "Text", "text": text, "toolName": None, "toolId": None}],
            "timestamp": None,
        })
    return web.json_response({"ok": True})


async def post_escape(request):
    log_request("POST", request.path, request.headers, await request.text())
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response({"ok": True})


async def post_interrupt(request):
    log_request("POST", request.path, request.headers, await request.text())
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response({"ok": True})


# ----- dictation (Gateway-native /wingman/utterance/* flow the DictationDialog uses) -----
async def utterance_upload(request):
    log_request("POST", request.path, request.headers, None)
    return web.json_response({"upload_id": "up_811"})


async def utterance_chunk(request):
    await request.read()
    log_request("PUT", request.path, request.headers, "<bytes>")
    return web.json_response({"ok": True})


async def utterance_complete(request):
    log_request("POST", request.path, request.headers, await request.text())
    return web.json_response({"transcript": "hello from dictation"})


# ----- raw terminal mirror WS (size frame + a binary banner) so AC1/AC9 show the raw view -----
async def stream_ws(request):
    ws = web.WebSocketResponse()
    await ws.prepare(request)
    log_request("WS", request.path, request.headers, None)
    await ws.send_str(json.dumps({"type": "size", "cols": 80, "rows": 24}))
    banner = (
        "Proof Session 811 - RAW TERMINAL MIRROR\r\n"
        "$ claude --resume\r\n"
        "Welcome to the session. This is the raw PTY byte stream (not cleaned history).\r\n"
        "> _\r\n"
    )
    await ws.send_bytes(banner.encode("utf-8"))
    try:
        async for _ in ws:
            pass
    except Exception:
        pass
    return ws


# ----- static bundle at /m (with token injection mirroring the Gateway) -----
def read_index():
    html = (DIST / "index.html").read_text(encoding="utf-8")
    return html.replace("__GATEWAY_TOKEN__", TEST_TOKEN if AUTH else "__GATEWAY_TOKEN__")


async def serve_index(request):
    return web.Response(text=read_index(), content_type="text/html")


async def serve_asset(request):
    rel = request.match_info["path"]
    target = (DIST / rel).resolve()
    if not str(target).startswith(str(DIST.resolve())) or not target.is_file():
        # SPA deep-link fallback (e.g. /m/session/<id>/chat) -> the injected shell.
        return await serve_index(request)
    ctype = {
        ".js": "application/javascript", ".css": "text/css", ".html": "text/html",
        ".json": "application/json", ".webmanifest": "application/manifest+json",
        ".png": "image/png", ".svg": "image/svg+xml", ".woff2": "font/woff2",
    }.get(target.suffix, "application/octet-stream")
    return web.Response(body=target.read_bytes(), content_type=ctype)


def make_app():
    app = web.Application()
    app.router.add_get("/sessions", get_sessions)
    app.router.add_get("/sessions/{sid}/stream", stream_ws)
    app.router.add_get("/sessions/{sid}/history", get_history)
    app.router.add_post("/sessions/{sid}/prompt", post_prompt)
    app.router.add_post("/sessions/{sid}/escape", post_escape)
    app.router.add_post("/sessions/{sid}/interrupt", post_interrupt)
    app.router.add_post("/wingman/utterance/upload", utterance_upload)
    app.router.add_put("/wingman/utterance/{id}/chunk/{n}", utterance_chunk)
    app.router.add_post("/wingman/utterance/{id}/complete", utterance_complete)
    app.router.add_get("/m", serve_index)
    app.router.add_get("/m/", serve_index)
    app.router.add_get("/m/index.html", serve_index)
    app.router.add_get("/m/{path:.*}", serve_asset)
    return app


if __name__ == "__main__":
    LOG.write_text("", encoding="utf-8")
    web.run_app(make_app(), host="127.0.0.1", port=PORT, print=lambda *a: None)
