# /// script
# requires-python = ">=3.11"
# dependencies = ["msal>=1.28", "requests>=2.31"]
# ///
"""Mail Desk phase 0, proof (b): read-only measurements of the email@devthrottle.com mailbox.

It READS ONLY. Every Microsoft Graph call is an HTTP GET; the guard in GetOnlyGraphSession refuses any
other method, and refuses any host other than graph.microsoft.com, before a byte is sent. The guard is
tested by --self-test, which needs no network and runs automatically before every measurement run.

The saved sign-in (cc-outlook's MSAL token cache) is opened READ-ONLY: it is loaded into memory and never
written back, even when MSAL renews the access token. No interactive sign-in is ever attempted: if a silent
token cannot be had, the script stops and says so.

Nothing it prints or writes contains a message id, an address, a subject or a preview - only counts,
sizes, times and distributions. The raw messages live only in memory for the length of the run.

Usage:
  uv run --script measure_mailbox.py --self-test
  uv run --script measure_mailbox.py --out results/measurements.json
"""

import argparse
import hashlib
import json
import statistics
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlparse

import msal
import requests

MAILBOX = "email@devthrottle.com"
CONFIG_DIR = Path.home() / ".local/share/cc-director/instances/default/config/outlook"
AUTHORITY = "https://login.microsoftonline.com/common"
# The mission (5.4) asks only for Mail.ReadWrite. The guard, not the scope, is what keeps this run read-only.
SCOPES = ["https://graph.microsoft.com/Mail.ReadWrite"]
GRAPH = "https://graph.microsoft.com/v1.0"
GRAPH_HOST = "graph.microsoft.com"

INBOX_LIMIT = 200
INBOX_DAYS = 30
INBOX_PAGE_SIZE = 50
SENT_PAGE_SIZE = 100
SENT_CAP = 20000
PREVIEW_CHARS = 200
SHORT_KEY_CHARS = 12
CONTEXT_CAP = 8 * 1024
OUTPUT_CAP = 16 * 1024
INBOX_SELECT = "id,receivedDateTime,from,subject,hasAttachments,bodyPreview"


class RefusedRequest(Exception):
    pass


class GetOnlyGraphSession(requests.Session):
    """A requests session that can only send GET requests to Microsoft Graph."""

    def request(self, method, url, *args, **kwargs):
        if str(method).upper() != "GET":
            raise RefusedRequest(f"refused: method {method} is not GET")
        host = urlparse(url).hostname or ""
        if host != GRAPH_HOST:
            raise RefusedRequest(f"refused: host {host!r} is not {GRAPH_HOST}")
        kwargs["allow_redirects"] = False
        return super().request(method, url, *args, **kwargs)


def self_test() -> None:
    session = GetOnlyGraphSession()
    for method in ("POST", "PATCH", "PUT", "DELETE", "post", "HEAD", "OPTIONS"):
        try:
            session.request(method, f"{GRAPH}/me/messages/x/move", json={"destinationId": "archive"})
        except RefusedRequest:
            continue
        raise SystemExit(f"self-test FAILED: {method} was not refused")
    for helper in (session.post, session.patch, session.put, session.delete):
        try:
            helper(f"{GRAPH}/me/messages/x")
        except RefusedRequest:
            continue
        raise SystemExit(f"self-test FAILED: session.{helper.__name__} was not refused")
    try:
        session.get("https://gateway.devthrottle.com/health")
    except RefusedRequest:
        pass
    else:
        raise SystemExit("self-test FAILED: a GET to another host was not refused")
    print("self-test passed: POST, PATCH, PUT, DELETE, HEAD, OPTIONS and other hosts are refused before sending")


def token_path() -> Path:
    safe = MAILBOX.replace("@", "_").replace(".", "_")
    return CONFIG_DIR / "tokens" / f"{safe}_msal.json"


def client_id() -> str:
    profiles = json.loads((CONFIG_DIR / "profiles.json").read_text(encoding="utf-8"))
    found = (profiles.get("profiles", {}).get(MAILBOX) or {}).get("client_id")
    if not found:
        raise SystemExit(f"no cc-outlook profile with a client id for {MAILBOX}")
    return found


def access_token() -> str:
    path = token_path()
    if not path.exists():
        raise SystemExit(f"{MAILBOX} is not signed in: no saved sign-in at {path}")
    cache = msal.SerializableTokenCache()
    cache.deserialize(path.read_text(encoding="utf-8"))  # read-only: never serialized back
    app = msal.PublicClientApplication(client_id(), authority=AUTHORITY, token_cache=cache)
    accounts = [a for a in app.get_accounts() if a.get("username", "").lower() == MAILBOX]
    if not accounts:
        raise SystemExit(f"the saved sign-in holds no account for {MAILBOX}")
    result = app.acquire_token_silent(SCOPES, account=accounts[0])
    if not result or "access_token" not in result:
        reason = (result or {}).get("error", "no token")
        raise SystemExit(f"silent sign-in failed ({reason}); run cc-outlook -a devthrottle auth")
    return result["access_token"]


class Graph:
    def __init__(self, token: str):
        self.session = GetOnlyGraphSession()
        self.session.headers["Authorization"] = f"Bearer {token}"
        self.calls = 0
        self.throttled = 0

    def get(self, url: str, params=None) -> dict:
        for _ in range(5):
            self.calls += 1
            response = self.session.get(url, params=params, timeout=60)
            if response.status_code == 429:
                self.throttled += 1
                time.sleep(int(response.headers.get("Retry-After", "5")))
                continue
            if response.status_code != 200:
                error = response.json().get("error", {}) if response.content else {}
                raise RuntimeError(f"GET returned {response.status_code}: {error.get('code')} {error.get('message')}")
            return response.json()
        raise RuntimeError("throttled five times in a row")


def folder_counts(graph: Graph, folder: str) -> dict:
    data = graph.get(f"{GRAPH}/me/mailFolders/{folder}", {"$select": "totalItemCount,unreadItemCount"})
    return {"totalItemCount": data["totalItemCount"], "unreadItemCount": data["unreadItemCount"]}


def parse_time(text: str) -> datetime:
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def page_inbox(graph: Graph, now: datetime) -> dict:
    """Newest first, until 200 messages or 30 days back, whichever is fewer."""
    cutoff = now - timedelta(days=INBOX_DAYS)
    url = f"{GRAPH}/me/mailFolders/inbox/messages"
    params = {"$select": INBOX_SELECT, "$orderby": "receivedDateTime desc", "$top": str(INBOX_PAGE_SIZE)}
    messages, page_sizes, page_times, ordered = [], [], [], True
    stopped_by = None
    started = time.perf_counter()
    while url:
        t = time.perf_counter()
        data = graph.get(url, params)
        page_times.append(round(time.perf_counter() - t, 3))
        params = None  # the nextLink carries every query option
        page = data.get("value", [])
        page_sizes.append(len(page))
        for m in page:
            if messages and parse_time(m["receivedDateTime"]) > parse_time(messages[-1]["receivedDateTime"]):
                ordered = False
            if parse_time(m["receivedDateTime"]) < cutoff:
                stopped_by = "30 days"
                break
            messages.append(m)
            if len(messages) >= INBOX_LIMIT:
                stopped_by = "200 messages"
                break
        if stopped_by:
            break
        url = data.get("@odata.nextLink")
    if not stopped_by:
        stopped_by = "end of folder"
    oldest = parse_time(messages[-1]["receivedDateTime"]) if messages else None
    return {
        "messages": messages,
        "report": {
            "pageSizeRequested": INBOX_PAGE_SIZE,
            "pages": len(page_sizes),
            "messagesPerPage": page_sizes,
            "secondsPerPage": page_times,
            "secondsTotal": round(time.perf_counter() - started, 3),
            "messagesYielded": len(messages),
            "stoppedBy": stopped_by,
            "oldestYieldedDaysBack": round((now - oldest).total_seconds() / 86400, 2) if oldest else None,
            "nextLinkKeptNewestFirstOrder": ordered,
        },
    }


def probe_inbox_limits(graph: Graph, now: datetime) -> dict:
    """What Graph allows: a large $top, and $filter on receivedDateTime together with the same $orderby."""
    url = f"{GRAPH}/me/mailFolders/inbox/messages"
    probes = {}
    for top in (1000, 1001):
        try:
            t = time.perf_counter()
            data = graph.get(url, {"$select": "id,receivedDateTime", "$orderby": "receivedDateTime desc", "$top": str(top)})
            probes[f"top{top}"] = {"accepted": True, "returned": len(data.get("value", [])),
                                   "hasNextLink": "@odata.nextLink" in data,
                                   "seconds": round(time.perf_counter() - t, 3)}
        except RuntimeError as e:
            probes[f"top{top}"] = {"accepted": False, "error": str(e)[:200]}
    since = (now - timedelta(days=INBOX_DAYS)).strftime("%Y-%m-%dT%H:%M:%SZ")
    try:
        data = graph.get(url, {"$select": "id,receivedDateTime", "$filter": f"receivedDateTime ge {since}",
                               "$orderby": "receivedDateTime desc", "$top": "50"})
        probes["filterReceivedSince30DaysWithOrderby"] = {"accepted": True, "firstPage": len(data.get("value", [])),
                                                          "hasNextLink": "@odata.nextLink" in data}
    except RuntimeError as e:
        probes["filterReceivedSince30DaysWithOrderby"] = {"accepted": False, "error": str(e)[:200]}
    try:
        data = graph.get(url, {"$select": "id,receivedDateTime", "$filter": f"receivedDateTime ge {since}",
                               "$orderby": "receivedDateTime asc", "$top": "50"})
        probes["filterReceivedSince30DaysOldestFirst"] = {"accepted": True, "firstPage": len(data.get("value", [])),
                                                          "hasNextLink": "@odata.nextLink" in data}
    except RuntimeError as e:
        probes["filterReceivedSince30DaysOldestFirst"] = {"accepted": False, "error": str(e)[:200]}
    return probes


def probe_small_pages(graph: Graph, folder: str, field: str, top: int) -> dict:
    """The mailbox is small, so walk it in tiny pages to prove @odata.nextLink paging keeps the $orderby."""
    url = f"{GRAPH}/me/mailFolders/{folder}/messages"
    params = {"$select": f"id,{field}", "$orderby": f"{field} desc", "$top": str(top)}
    seen, times, pages, ordered, repeats = [], [], 0, True, 0
    ids = set()
    started = time.perf_counter()
    while url:
        data = graph.get(url, params)
        params = None
        pages += 1
        for m in data.get("value", []):
            if m["id"] in ids:
                repeats += 1
            ids.add(m["id"])
            if times and parse_time(m[field]) > times[-1]:
                ordered = False
            times.append(parse_time(m[field]))
        url = data.get("@odata.nextLink")
    return {"pageSize": top, "pages": pages, "messages": len(times), "distinctMessages": len(ids),
            "repeatedAcrossPages": repeats, "newestFirstOrderKept": ordered,
            "secondsTotal": round(time.perf_counter() - started, 3)}


def walk_sent(graph: Graph, now: datetime) -> tuple[set, dict]:
    url = f"{GRAPH}/me/mailFolders/sentitems/messages"
    params = {"$select": "sentDateTime,toRecipients,ccRecipients,bccRecipients",
              "$orderby": "sentDateTime desc", "$top": str(SENT_PAGE_SIZE)}
    recipients, count, pages, oldest = set(), 0, 0, None
    started = time.perf_counter()
    while url and count < SENT_CAP:
        data = graph.get(url, params)
        params = None
        pages += 1
        for m in data.get("value", []):
            count += 1
            if m.get("sentDateTime"):
                oldest = parse_time(m["sentDateTime"])
            for field in ("toRecipients", "ccRecipients", "bccRecipients"):
                for r in m.get(field) or []:
                    address = (r.get("emailAddress") or {}).get("address")
                    if address:
                        recipients.add(address.strip().lower())
        url = data.get("@odata.nextLink")
    return recipients, {
        "pageSizeRequested": SENT_PAGE_SIZE,
        "pages": pages,
        "messages": count,
        "cappedAt": SENT_CAP if url else None,
        "oldestDaysBack": round((now - oldest).total_seconds() / 86400, 1) if oldest else None,
        "distinctRecipientAddresses": len(recipients),
        "secondsTotal": round(time.perf_counter() - started, 3),
    }


def check_record(m: dict, known: set, preview_chars: int) -> dict:
    sender = (m.get("from") or {}).get("emailAddress") or {}
    address = (sender.get("address") or "").strip().lower()
    return {
        "key": hashlib.sha256(m["id"].encode("utf-8")).hexdigest()[:SHORT_KEY_CHARS],
        "received": m["receivedDateTime"],
        "from": sender.get("address") or "",
        "name": sender.get("name") or "",
        "subject": m.get("subject") or "",
        "hasAttachments": bool(m.get("hasAttachments")),
        "preview": (m.get("bodyPreview") or "")[:preview_chars],
        "knownSender": address in known,
    }


def dumps(value, ascii_only: bool) -> str:
    return json.dumps(value, ensure_ascii=ascii_only, separators=(",", ":"))


def size(text: str) -> int:
    return len(text.encode("utf-8"))


def distribution(values: list[int]) -> dict:
    ordered = sorted(values)
    if not ordered:
        return {}
    p90 = ordered[min(len(ordered) - 1, int(round(0.9 * (len(ordered) - 1))))]
    return {"n": len(ordered), "min": ordered[0], "median": statistics.median(ordered), "p90": p90,
            "max": ordered[-1], "mean": round(statistics.mean(ordered), 1)}


def output_sizes(records: list, ascii_only: bool) -> tuple[int, int]:
    context = dumps({"messages": records}, ascii_only)
    output = dumps({"count": len(records), "context": context}, ascii_only)
    return size(context), size(output)


def fit_uniform(record: dict, ascii_only: bool) -> int:
    """How many copies of one record fit both caps."""
    n = 0
    while True:
        context, output = output_sizes([record] * (n + 1), ascii_only)
        if context > CONTEXT_CAP or output > OUTPUT_CAP:
            return n
        n += 1


def fit_greedy(records: list, ascii_only: bool) -> list[int]:
    """Real batches, oldest first as the check reports them: how many messages each batch holds."""
    batches, start = [], 0
    while start < len(records):
        n = 0
        while start + n < len(records):
            context, output = output_sizes(records[start:start + n + 1], ascii_only)
            if context > CONTEXT_CAP or output > OUTPUT_CAP:
                break
            n += 1
        if n == 0:
            raise RuntimeError("one message alone does not fit the caps")
        batches.append(n)
        start += n
    return batches


def measure_sizes(messages: list, known: set) -> dict:
    ids = [len(m["id"]) for m in messages]
    report = {"messageIdLength": distribution(ids), "byPreviewLength": {}}
    for preview in (200, 100):
        records = [check_record(m, known, preview) for m in messages]
        per = {}
        for ascii_only, label in ((False, "utf8"), (True, "asciiEscaped")):
            sizes = [size(dumps(r, ascii_only)) for r in records]
            escaped = [size(json.dumps(dumps(r, ascii_only), ensure_ascii=ascii_only)) - 2 for r in records]
            by_size = sorted(range(len(records)), key=lambda i: sizes[i])
            median_record = records[by_size[len(by_size) // 2]]
            worst_record = records[by_size[-1]]
            oldest_first = list(reversed(records))
            batches = fit_greedy(oldest_first, ascii_only)
            per[label] = {
                "recordBytesInContext": distribution(sizes),
                "recordBytesEscapedInOutput": distribution(escaped),
                "fitOneContextMedianRecord": fit_uniform(median_record, ascii_only),
                "fitOneContextWorstRecord": fit_uniform(worst_record, ascii_only),
                "realBatchesOverSample": {"batches": len(batches), "lastBatchRemainder": batches[-1],
                                          "messagesPerFullBatch": distribution(batches[:-1] or batches)},
                "bindingCapForWorstRecord": binding_cap(worst_record, ascii_only),
            }
        report["byPreviewLength"][str(preview)] = per
    return report


def binding_cap(record: dict, ascii_only: bool) -> str:
    n = fit_uniform(record, ascii_only)
    context, output = output_sizes([record] * (n + 1), ascii_only)
    return "context 8 KB" if context > CONTEXT_CAP else "output 16 KB"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    self_test()
    if args.self_test:
        return 0

    now = datetime.now(timezone.utc)
    graph = Graph(access_token())
    started = time.perf_counter()
    before = folder_counts(graph, "inbox")
    inbox = page_inbox(graph, now)
    probes = probe_inbox_limits(graph, now)
    probes["inboxSmallPages"] = probe_small_pages(graph, "inbox", "receivedDateTime", 5)
    probes["sentSmallPages"] = probe_small_pages(graph, "sentitems", "sentDateTime", 10)
    known, sent = walk_sent(graph, now)
    sizes = measure_sizes(inbox["messages"], known)
    known_in_sample = sum(1 for m in inbox["messages"]
                          if ((m.get("from") or {}).get("emailAddress") or {}).get("address", "").strip().lower() in known)
    after = folder_counts(graph, "inbox")
    result = {
        "mailbox": MAILBOX,
        "measuredAt": now.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "inboxCountsBefore": before,
        "inboxCountsAfter": after,
        "inboxCountsUnchanged": before == after,
        "inboxPaging": inbox["report"],
        "inboxProbes": probes,
        "sentFolder": sent,
        "knownSendersInInboxSample": known_in_sample,
        "sizes": sizes,
        "caps": {"contextBytes": CONTEXT_CAP, "outputBytes": OUTPUT_CAP},
        "graphCalls": graph.calls,
        "throttledResponses": graph.throttled,
        "secondsWholeRun": round(time.perf_counter() - started, 3),
    }
    text = json.dumps(result, indent=2)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
