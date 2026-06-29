"""
Mock Gateway for issue #812 session-management proof.

Serves the REAL built mobile PWA bundle (mobile/dist) at /m, exactly as the Gateway does, and
faithfully mocks the endpoints the add-session flow + Hold/Resume + Remove buttons talk to:

  GET    /sessions                       - the live roster (mutable: a created session appears,
                                           a removed one is gone, a held one shows onHold=true)
  GET    /directors                      - the fleet's machines (Step 1 picker)
  GET    /directors/{id}/repos           - a machine's recent repos, newest-first (Step 2 picker)
  POST   /directors/{id}/sessions        - create a session -> appended to the roster, returns the
                                           new SessionDto (201)
  POST   /sessions/{sid}/hold            - toggle on-hold, returns { onHold }
  DELETE /sessions/{sid}                 - kill + remove, returns { killed, removed }
  GET    /sessions/{sid}/history         - minimal supported history so the Chat view renders
  WS     /sessions/{sid}/stream          - a raw banner so the Terminal view renders

Every request is logged (method, path, Authorization header, body) to the given jsonl log so the AC
captures (the list/create/hold/remove 2xx + the Bearer in both auth modes) come straight from the
wire.

Run: python mock_gateway.py <dist_dir> <port> <auth on|off> <log_path>
"""
import json
import sys
from pathlib import Path
from aiohttp import web

DIST = Path(sys.argv[1])
PORT = int(sys.argv[2])
AUTH = sys.argv[3] == "on"
LOG = Path(sys.argv[4])
TEST_TOKEN = "test-gateway-token-812"

# A pre-existing session so the Hold/Resume + Remove buttons have something to act on at start.
EXISTING_SID = "812e0000-0000-0000-0000-000000000001"

DIRECTORS = [
    {"directorId": "dir-north", "machineName": "soren-north", "version": "0.9.27",
     "tailnetEndpoint": "http://100.64.0.10:7878", "controlEndpoint": "http://127.0.0.1:7879",
     "lastSeen": "2026-06-29T12:30:00Z"},
    {"directorId": "dir-south", "machineName": "soren-south", "version": "0.9.26",
     "tailnetEndpoint": "http://100.64.0.11:7878", "controlEndpoint": "http://127.0.0.1:7880",
     "lastSeen": "2026-06-29T11:05:00Z"},
]

REPOS = {
    "dir-north": [
        {"name": "devthrottle", "path": "D:\\ReposFred\\devthrottle", "lastUsed": "2026-06-29T12:25:00Z"},
        {"name": "mindzieStudio", "path": "D:\\ReposMindzie\\mindzieStudio", "lastUsed": "2026-06-28T09:10:00Z"},
        {"name": "swimframe", "path": "D:\\ReposOther\\swimframe", "lastUsed": "2026-06-20T16:40:00Z"},
    ],
    "dir-south": [
        {"name": "cencon-docs", "path": "D:\\ReposFred\\cencon-docs", "lastUsed": "2026-06-27T14:00:00Z"},
    ],
}


def new_session_dto(sid, name, director_id, repo_path, on_hold=False):
    return {
        "sessionId": sid, "name": name, "directorId": director_id, "agent": "ClaudeCode",
        "repoPath": repo_path, "status": "Idle", "activityState": "WaitingForInput",
        "statusColor": "green", "onHold": on_hold, "sortOrder": 0,
        "createdAt": "2026-06-29T12:00:00Z", "machineName": "soren-north",
    }


# Mutable roster keyed by sessionId.
ROSTER = {
    EXISTING_SID: new_session_dto(EXISTING_SID, "Existing Session 812", "dir-north", "D:\\ReposFred\\devthrottle"),
}
CREATE_COUNTER = {"n": 0}


def log_request(method, path, headers, body):
    rec = {"method": method, "path": path,
           "authorization": headers.get("Authorization", None), "body": body}
    with LOG.open("a", encoding="utf-8") as f:
        f.write(json.dumps(rec) + "\n")


async def maybe_auth(request):
    """In auth-on mode, require the Bearer the injected page must attach (proves the client sends it)."""
    if not AUTH:
        return None
    if request.headers.get("Authorization", "") != f"Bearer {TEST_TOKEN}":
        return web.json_response({"error": "missing bearer"}, status=401)
    return None


async def get_sessions(request):
    log_request("GET", "/sessions", request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response(list(ROSTER.values()))


async def get_directors(request):
    log_request("GET", "/directors", request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response(DIRECTORS)


async def get_repos(request):
    did = request.match_info["id"]
    log_request("GET", request.path, request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response(REPOS.get(did, []))


async def create_session(request):
    did = request.match_info["id"]
    body = await request.text()
    log_request("POST", request.path, request.headers, body)
    denied = await maybe_auth(request)
    if denied:
        return denied
    try:
        data = json.loads(body)
    except Exception:
        data = {}
    repo_path = data.get("repoPath", "")
    if not repo_path:
        return web.json_response({"error": "repoPath is required"}, status=400)
    CREATE_COUNTER["n"] += 1
    n = CREATE_COUNTER["n"]
    sid = f"812c0000-0000-0000-0000-{n:012d}"
    leaf = repo_path.replace("/", "\\").rstrip("\\").split("\\")[-1] or repo_path
    dto = new_session_dto(sid, f"new {leaf}", did, repo_path)
    ROSTER[sid] = dto
    return web.json_response(dto, status=201)


async def hold_session(request):
    sid = request.match_info["sid"]
    body = await request.text()
    log_request("POST", request.path, request.headers, body)
    denied = await maybe_auth(request)
    if denied:
        return denied
    try:
        data = json.loads(body) if body else {}
    except Exception:
        data = {}
    cur = ROSTER.get(sid)
    if cur is None:
        return web.json_response({"error": "session not found"}, status=404)
    on_hold = data.get("onHold", not cur.get("onHold", False))
    cur["onHold"] = bool(on_hold)
    cur["statusColor"] = "grey" if on_hold else "green"
    return web.json_response({"onHold": cur["onHold"]})


async def delete_session(request):
    sid = request.match_info["sid"]
    log_request("DELETE", request.path, request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    existed = ROSTER.pop(sid, None) is not None
    return web.json_response({"killed": existed, "removed": existed})


async def get_history(request):
    sid = request.match_info["sid"]
    log_request("GET", request.path, request.headers, None)
    denied = await maybe_auth(request)
    if denied:
        return denied
    return web.json_response({
        "sessionId": sid, "directorId": "dir-north", "agent": "ClaudeCode",
        "isSupported": True, "isRawText": False, "historyState": "Idle", "status": "ok", "error": None,
        "messages": [
            {"role": "User", "parts": [{"kind": "Text", "text": "Hello from the proof session.",
                                        "toolName": None, "toolId": None}], "timestamp": None},
            {"role": "Assistant", "parts": [{"kind": "Text", "text": "Ready.",
                                             "toolName": None, "toolId": None}], "timestamp": None},
        ],
    })


async def stream_ws(request):
    ws = web.WebSocketResponse()
    await ws.prepare(request)
    log_request("WS", request.path, request.headers, None)
    await ws.send_str(json.dumps({"type": "size", "cols": 80, "rows": 24}))
    await ws.send_bytes(b"Proof Session 812 - RAW TERMINAL MIRROR\r\n$ claude --resume\r\n> _\r\n")
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
        return await serve_index(request)  # SPA deep-link fallback -> the injected shell
    ctype = {
        ".js": "application/javascript", ".css": "text/css", ".html": "text/html",
        ".json": "application/json", ".webmanifest": "application/manifest+json",
        ".png": "image/png", ".svg": "image/svg+xml", ".woff2": "font/woff2",
    }.get(target.suffix, "application/octet-stream")
    return web.Response(body=target.read_bytes(), content_type=ctype)


def make_app():
    app = web.Application()
    app.router.add_get("/sessions", get_sessions)
    app.router.add_get("/directors", get_directors)
    app.router.add_get("/directors/{id}/repos", get_repos)
    app.router.add_post("/directors/{id}/sessions", create_session)
    app.router.add_post("/sessions/{sid}/hold", hold_session)
    app.router.add_delete("/sessions/{sid}", delete_session)
    app.router.add_get("/sessions/{sid}/history", get_history)
    app.router.add_get("/sessions/{sid}/stream", stream_ws)
    app.router.add_get("/m", serve_index)
    app.router.add_get("/m/", serve_index)
    app.router.add_get("/m/index.html", serve_index)
    app.router.add_get("/m/{path:.*}", serve_asset)
    return app


if __name__ == "__main__":
    LOG.write_text("", encoding="utf-8")
    web.run_app(make_app(), host="127.0.0.1", port=PORT, print=lambda *a: None)
