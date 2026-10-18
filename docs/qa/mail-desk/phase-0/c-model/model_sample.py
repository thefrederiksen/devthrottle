# /// script
# requires-python = ">=3.11"
# dependencies = ["msal>=1.28", "httpx>=0.27"]
# ///
"""Mail Desk phase 0, proof (c): the model on a sample.

Reads the 50 most recent Inbox emails of email@devthrottle.com with the saved cc-outlook sign-in,
opened READ-ONLY (the token cache is never written back), using GET requests only, and asks the
model the two questions of mission section 5.7 for every email.

Subcommands, run in order:
  fetch     read the emails into the raw folder (outside the repository)
  classify  ask the model both questions for every email in the raw folder
  show      print each email with the model's answers, on screen only, for judging by hand
  report    compare the model with the hand labels and write the committed results

Raw text (addresses, subjects, previews) is written ONLY to --raw-dir, which must be outside the
repository. Committed output carries short keys, sender kinds, labels, confidences, tokens and cost.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[4]
PROMPTS = HERE / "prompts"
RESULTS = HERE / "results"

OUR_ADDRESS = "email@devthrottle.com"
OUR_DOMAIN = "devthrottle.com"
GRAPH_HOST = "graph.microsoft.com"
GRAPH_SCOPES = ["https://graph.microsoft.com/Mail.ReadWrite"]
AUTHORITY = "https://login.microsoftonline.com/common"
SAMPLE_SIZE = 50
PREVIEW_CHARS = 200

# Prices of devthrottle/wingman-fast = Qwen/Qwen2.5-72B-Instruct on DeepInfra, dollars per token.
PRICE_INPUT = 0.36 / 1_000_000
PRICE_OUTPUT = 0.40 / 1_000_000
SPEND_LIMIT = 0.50

ENDPOINTS = {
    # The website's inference proxy, by the included name. Needs a dt_ key.
    "proxy": {"base": "https://devthrottle.com/api/v1", "model": "devthrottle/wingman-fast"},
    # DeepInfra directly, with the model the included name maps to. Needs a DeepInfra key.
    "deepinfra": {"base": "https://api.deepinfra.com/v1/openai", "model": "Qwen/Qwen2.5-72B-Instruct"},
    # A deterministic stand-in for testing the script without spending anything.
    "fake": {"base": "", "model": "fake"},
}

PERSONAL_DOMAINS = {
    "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yahoo.com",
    "ymail.com", "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com",
    "gmx.com", "gmx.net", "gmx.de", "mail.com", "zoho.com", "yandex.com", "hey.com", "fastmail.com",
}


# ---------------------------------------------------------------------------------------------
# The guard: the only call this script may make against Microsoft Graph is a GET.
# ---------------------------------------------------------------------------------------------

class ForbiddenGraphCall(Exception):
    pass


def graph_guard(method: str, url: str) -> None:
    """Refuse any method other than GET against graph.microsoft.com. Keep this."""
    import httpx

    host = httpx.URL(url).host.lower()
    if host == GRAPH_HOST or host.endswith("." + GRAPH_HOST):
        if method.upper() != "GET":
            raise ForbiddenGraphCall(f"refused: {method.upper()} against {GRAPH_HOST} - only GET is allowed")


def graph_get(client, url: str, token: str) -> dict:
    graph_guard("GET", url)
    resp = client.request("GET", url, headers={"Authorization": f"Bearer {token}"})
    if resp.status_code != 200:
        raise RuntimeError(f"Graph GET failed: HTTP {resp.status_code}: {resp.text[:300]}")
    return resp.json()


# ---------------------------------------------------------------------------------------------
# Small pure helpers (tested in test_model_sample.py)
# ---------------------------------------------------------------------------------------------

def short_key(message_id: str) -> str:
    return hashlib.sha256(message_id.encode("utf-8")).hexdigest()[:12]


def sender_kind(address: str) -> str:
    address = (address or "").strip().lower()
    if "@" not in address:
        return "no-address"
    domain = address.rsplit("@", 1)[1]
    if domain == OUR_DOMAIN or domain.endswith("." + OUR_DOMAIN):
        return "ours"
    if domain in PERSONAL_DOMAINS:
        return "personal-domain"
    return domain


def parse_answer(content: str | None, letters: list[str]) -> str | None:
    """The letter the model answered, or None (counts as unsure) for anything else."""
    if content is None:
        return None
    text = content.strip().rstrip(".").strip().upper()
    return text if text in letters else None


def confidence_from_logprobs(logprobs: dict | None, letters: list[str]) -> tuple[float | None, dict]:
    """Probability of the answered first token, and the probability of each letter from top_logprobs."""
    if not logprobs or not logprobs.get("content"):
        return None, {}
    first = logprobs["content"][0]
    chosen = math.exp(first["logprob"])
    per_letter: dict[str, float] = {}
    for alt in first.get("top_logprobs") or []:
        tok = alt["token"].strip().upper()
        if tok in letters:
            per_letter[tok] = per_letter.get(tok, 0.0) + math.exp(alt["logprob"])
    return chosen, per_letter


def call_cost(usage: dict) -> float:
    return usage.get("prompt_tokens", 0) * PRICE_INPUT + usage.get("completion_tokens", 0) * PRICE_OUTPUT


def render(template: str, email: dict, **extra) -> str:
    return template.format(
        sender_name=email.get("sender_name") or "",
        sender_address=email.get("sender_address") or "",
        subject=email.get("subject") or "",
        preview=(email.get("preview") or "")[:PREVIEW_CHARS],
        **extra,
    )


def load_questions() -> list[dict]:
    """The two questions, each with its letters, label names and prompt template."""
    routes = json.loads((PROMPTS / "routes.json").read_text(encoding="utf-8"))
    route_labels = [r["name"] for r in routes["routes"]] + [routes["unsure"]["name"]]
    route_handles = [r["handles"] for r in routes["routes"]] + [routes["unsure"]["handles"]]
    route_letters = [chr(ord("A") + i) for i in range(len(route_labels))]
    route_lines = "\n".join(f"{l}. {h}" for l, h in zip(route_letters, route_handles))
    route_template = (PROMPTS / "route.txt").read_text(encoding="utf-8")
    return [
        {
            "id": "person_or_spam",
            "letters": ["A", "B"],
            "labels": ["person", "spam"],
            "template": (PROMPTS / "person_or_spam.txt").read_text(encoding="utf-8"),
            "extra": {},
        },
        {
            "id": "route",
            "letters": route_letters,
            "labels": route_labels,
            "template": route_template,
            "extra": {"route_lines": route_lines, "letters": ", ".join(route_letters[:-1]) + " or " + route_letters[-1]},
        },
    ]


def check_raw_dir(raw_dir: Path) -> Path:
    raw_dir = raw_dir.resolve()
    if raw_dir == REPO_ROOT or REPO_ROOT in raw_dir.parents:
        sys.exit(f"refused: --raw-dir {raw_dir} is inside the repository; raw email text must stay outside it")
    raw_dir.mkdir(parents=True, exist_ok=True)
    os.chmod(raw_dir, 0o700)
    return raw_dir


# ---------------------------------------------------------------------------------------------
# fetch
# ---------------------------------------------------------------------------------------------

def cmd_fetch(args) -> None:
    import httpx
    import msal

    raw_dir = check_raw_dir(Path(args.raw_dir))
    config_dir = Path(args.outlook_config)
    profiles = json.loads((config_dir / "profiles.json").read_text(encoding="utf-8"))["profiles"]
    profile = profiles.get(OUR_ADDRESS)
    if not profile:
        sys.exit(f"{OUR_ADDRESS} has no cc-outlook profile in {config_dir}")
    cache_path = config_dir / "tokens" / f"{OUR_ADDRESS}_msal.json"
    if not cache_path.exists():
        sys.exit(f"{OUR_ADDRESS} is not signed in on this machine: run cc-outlook -a devthrottle auth")

    # READ-ONLY: the cache is deserialized into memory and never serialized back to disk.
    cache = msal.SerializableTokenCache()
    cache.deserialize(cache_path.read_text(encoding="utf-8"))
    app = msal.PublicClientApplication(client_id=profile["client_id"], authority=AUTHORITY, token_cache=cache)
    accounts = [a for a in app.get_accounts() if a.get("username", "").lower() == OUR_ADDRESS]
    if not accounts:
        sys.exit(f"the saved sign-in holds no account for {OUR_ADDRESS}: run cc-outlook -a devthrottle auth")
    result = app.acquire_token_silent(GRAPH_SCOPES, account=accounts[0])
    if not result or "access_token" not in result:
        reason = (result or {}).get("error", "no token")
        sys.exit(f"the saved sign-in for {OUR_ADDRESS} did not give a token ({reason}): run cc-outlook -a devthrottle auth")
    token = result["access_token"]

    url = (
        f"https://{GRAPH_HOST}/v1.0/me/mailFolders/inbox/messages"
        f"?$top={SAMPLE_SIZE}&$orderby=receivedDateTime desc"
        "&$select=id,receivedDateTime,from,subject,bodyPreview"
    )
    with httpx.Client(timeout=30) as client:
        page = graph_get(client, url, token)
    emails = []
    for m in page.get("value", [])[:SAMPLE_SIZE]:
        sender = (m.get("from") or {}).get("emailAddress") or {}
        emails.append({
            "key": short_key(m["id"]),
            "received": m.get("receivedDateTime"),
            "sender_address": sender.get("address") or "",
            "sender_name": sender.get("name") or "",
            "subject": m.get("subject") or "",
            "preview": (m.get("bodyPreview") or "")[:PREVIEW_CHARS],
        })
    out = raw_dir / "emails.json"
    out.write_text(json.dumps(emails, indent=1, ensure_ascii=False), encoding="utf-8")
    os.chmod(out, 0o600)
    print(f"fetch: {len(emails)} emails read with GET only; raw text kept outside the repository")


# ---------------------------------------------------------------------------------------------
# classify
# ---------------------------------------------------------------------------------------------

def fake_completion(prompt: str, letters: list[str]) -> dict:
    """Deterministic stand-in: picks a letter from a hash of the prompt, with made-up log probabilities."""
    h = int(hashlib.sha256(prompt.encode("utf-8")).hexdigest(), 16)
    pick = letters[h % len(letters)]
    p = 0.5 + (h % 50) / 100
    tops = [{"token": pick, "logprob": math.log(p)}] + [
        {"token": l, "logprob": math.log((1 - p) / (len(letters) - 1))} for l in letters if l != pick
    ]
    return {
        "choices": [{"message": {"content": pick}, "logprobs": {"content": [{"token": pick, "logprob": math.log(p), "top_logprobs": tops}]}}],
        "usage": {"prompt_tokens": len(prompt) // 4, "completion_tokens": 1},
    }


def cmd_classify(args) -> None:
    import httpx

    raw_dir = check_raw_dir(Path(args.raw_dir))
    emails = json.loads((raw_dir / "emails.json").read_text(encoding="utf-8"))
    endpoint = ENDPOINTS[args.endpoint]
    key = None
    if args.endpoint != "fake":
        key = os.environ.get(args.key_env)
        if not key:
            sys.exit(f"no key in the environment variable {args.key_env}")
    system = (PROMPTS / "system.txt").read_text(encoding="utf-8").strip()
    questions = load_questions()

    spent = 0.0
    rows = []
    stopped = None
    with httpx.Client(timeout=60) as client:
        for email in emails:
            row = {"key": email["key"], "sender_kind": sender_kind(email["sender_address"])}
            for q in questions:
                prompt = render(q["template"], email, **q["extra"])
                # Hard stop: refuse the call if a generous estimate of it would cross the limit.
                estimate = (len(system) + len(prompt)) / 2 * PRICE_INPUT + 5 * PRICE_OUTPUT
                if spent + estimate > SPEND_LIMIT:
                    stopped = f"spend limit ${SPEND_LIMIT:.2f} reached at ${spent:.6f}"
                    break
                body = {
                    "model": endpoint["model"],
                    "messages": [{"role": "system", "content": system}, {"role": "user", "content": prompt}],
                    "temperature": 0,
                    "max_tokens": 2,
                    "logprobs": True,
                    "top_logprobs": 5,
                }
                if args.endpoint == "fake":
                    data = fake_completion(prompt, q["letters"])
                else:
                    headers = {"Authorization": f"Bearer {key}"}
                    if args.endpoint == "proxy":
                        headers["x-devthrottle-feature"] = "mail-desk"
                    resp = client.post(f"{endpoint['base']}/chat/completions", json=body, headers=headers)
                    if resp.status_code != 200:
                        sys.exit(f"model call failed: HTTP {resp.status_code}: {resp.text[:300]}")
                    data = resp.json()
                choice = data["choices"][0]
                content = (choice.get("message") or {}).get("content")
                letter = parse_answer(content, q["letters"])
                confidence, per_letter = confidence_from_logprobs(choice.get("logprobs"), q["letters"])
                usage = data.get("usage") or {}
                cost = call_cost(usage)
                spent += cost
                label = q["labels"][q["letters"].index(letter)] if letter else "unsure"
                row[q["id"]] = {
                    "label": label,
                    "valid_answer": letter is not None,
                    "confidence": round(confidence, 6) if (confidence is not None and letter) else None,
                    "letter_probabilities": {q["labels"][q["letters"].index(l)]: round(p, 6) for l, p in per_letter.items()},
                    "prompt_tokens": usage.get("prompt_tokens"),
                    "completion_tokens": usage.get("completion_tokens"),
                    "cost_dollars": round(cost, 8),
                }
            if stopped:
                break
            rows.append(row)

    out = {
        "endpoint": args.endpoint,
        "model": endpoint["model"],
        "emails": len(rows),
        "spent_dollars": round(spent, 6),
        "spend_limit_dollars": SPEND_LIMIT,
        "stopped": stopped,
        "rows": rows,
    }
    # The fake model's answers are test output and stay in the raw folder, never in the committed results.
    target = raw_dir / "model_answers.fake.json" if args.endpoint == "fake" else RESULTS / "model_answers.json"
    target.write_text(json.dumps(out, indent=1), encoding="utf-8")
    print(f"classify: {len(rows)} emails, ${spent:.6f} spent, written to {target}" + (f"; STOPPED: {stopped}" if stopped else ""))


# ---------------------------------------------------------------------------------------------
# show (screen only) and report
# ---------------------------------------------------------------------------------------------

def cmd_show(args) -> None:
    raw_dir = check_raw_dir(Path(args.raw_dir))
    emails = json.loads((raw_dir / "emails.json").read_text(encoding="utf-8"))
    for i, e in enumerate(emails, 1):
        print(f"--- {i:2d} {e['key']}  {e['sender_name']} <{e['sender_address']}>")
        print(f"    subject: {e['subject']}")
        print(f"    preview: {e['preview'][:PREVIEW_CHARS]!r}")


def cmd_report(args) -> None:
    answers = json.loads(Path(args.answers).read_text(encoding="utf-8"))
    mine = json.loads(Path(args.my_labels).read_text(encoding="utf-8"))
    rows = []
    for r in answers["rows"]:
        m = mine.get(r["key"])
        if m is None:
            sys.exit(f"no hand label for {r['key']}")
        out = {
            "key": r["key"],
            "sender_kind": r["sender_kind"],
            "model_person_or_spam": r["person_or_spam"]["label"],
            "model_person_or_spam_confidence": r["person_or_spam"]["confidence"],
            "model_route": r["route"]["label"],
            "model_route_confidence": r["route"]["confidence"],
            "my_person_or_spam": m["person_or_spam"],
            "my_route": m["route"],
            "agree_person_or_spam": r["person_or_spam"]["label"] == m["person_or_spam"],
            "agree_route": r["route"]["label"] == m["route"],
            "tokens": sum((r[q]["prompt_tokens"] or 0) + (r[q]["completion_tokens"] or 0) for q in ("person_or_spam", "route")),
            "cost_dollars": round(r["person_or_spam"]["cost_dollars"] + r["route"]["cost_dollars"], 8),
        }
        rows.append(out)
    n = len(rows)
    summary = {
        "emails": n,
        "model": answers["model"],
        "endpoint": answers["endpoint"],
        "spent_dollars": answers["spent_dollars"],
        "agree_person_or_spam": sum(r["agree_person_or_spam"] for r in rows),
        "agree_route": sum(r["agree_route"] for r in rows),
        "confusion_person_or_spam": confusion(rows, "my_person_or_spam", "model_person_or_spam"),
        "confusion_route": confusion(rows, "my_route", "model_route"),
        "thresholds_route": threshold_table(rows, "route"),
        "thresholds_person_or_spam": threshold_table(rows, "person_or_spam"),
    }
    out_dir = Path(args.out)
    (out_dir / "per_email.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    print(json.dumps(summary, indent=1))


def confusion(rows: list[dict], truth: str, model: str) -> dict:
    table: dict[str, dict[str, int]] = {}
    for r in rows:
        table.setdefault(r[truth], {}).setdefault(r[model], 0)
        table[r[truth]][r[model]] += 1
    return table


def threshold_table(rows: list[dict], q: str) -> list[dict]:
    """For each candidate threshold: answers kept (confidence at or above it), and how many of those were wrong."""
    out = []
    for t in (0.0, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95, 0.99):
        kept = [r for r in rows if r[f"model_{q}"] != "unsure" and (r[f"model_{q}_confidence"] or 0) >= t]
        sent = [r for r in rows if r not in kept]
        out.append({
            "threshold": t,
            "kept": len(kept),
            "kept_wrong": sum(1 for r in kept if not r[f"agree_{q}"]),
            "sent_to_agent": len(sent),
            "sent_that_model_had_wrong": sum(1 for r in sent if not r[f"agree_{q}"]),
        })
    return out


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    default_config = Path.home() / ".local/share/cc-director/instances/default/config/outlook"
    f = sub.add_parser("fetch")
    f.add_argument("--raw-dir", required=True)
    f.add_argument("--outlook-config", default=str(default_config))
    c = sub.add_parser("classify")
    c.add_argument("--raw-dir", required=True)
    c.add_argument("--endpoint", choices=sorted(ENDPOINTS), required=True)
    c.add_argument("--key-env", default="MODEL_API_KEY", help="environment variable holding the key (never printed)")
    s = sub.add_parser("show")
    s.add_argument("--raw-dir", required=True)
    r = sub.add_parser("report")
    r.add_argument("--answers", default=str(RESULTS / "model_answers.json"))
    r.add_argument("--my-labels", default=str(RESULTS / "my_labels.json"))
    r.add_argument("--out", default=str(RESULTS))
    args = p.parse_args()
    {"fetch": cmd_fetch, "classify": cmd_classify, "show": cmd_show, "report": cmd_report}[args.cmd](args)


if __name__ == "__main__":
    main()
