# /// script
# requires-python = ">=3.11"
# dependencies = ["msal>=1.28", "httpx>=0.27", "pytest>=8"]
# ///
"""Tests for model_sample.py. Run: uv run --script test_model_sample.py"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import model_sample as ms  # noqa: E402


@pytest.mark.parametrize("method", ["POST", "PATCH", "PUT", "DELETE", "post", "OPTIONS"])
def test_graph_guard_non_get_against_graph_refused(method):
    with pytest.raises(ms.ForbiddenGraphCall):
        ms.graph_guard(method, "https://graph.microsoft.com/v1.0/me/messages/abc/move")


def test_graph_guard_get_against_graph_allowed():
    ms.graph_guard("GET", "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages")


def test_graph_guard_post_against_model_endpoint_allowed():
    ms.graph_guard("POST", "https://api.deepinfra.com/v1/openai/chat/completions")


def test_graph_get_routes_through_guard(monkeypatch):
    calls = []
    monkeypatch.setattr(ms, "graph_guard", lambda m, u: calls.append((m, u)))

    class FakeClient:
        def request(self, method, url, headers):
            class R:
                status_code = 200

                def json(self):
                    return {"value": []}
            return R()

    ms.graph_get(FakeClient(), "https://graph.microsoft.com/v1.0/me", "token")
    assert calls == [("GET", "https://graph.microsoft.com/v1.0/me")]


def test_short_key_is_twelve_hex_of_sha256():
    key = ms.short_key("AAMkAG-message-id")
    assert len(key) == 12 and all(c in "0123456789abcdef" for c in key)
    assert key == ms.short_key("AAMkAG-message-id")


@pytest.mark.parametrize("address,kind", [
    ("email@devthrottle.com", "ours"),
    ("Someone@Example.NET", "personal-domain"),
    ("alerts@example.com", "example.com"),
    ("", "no-address"),
])
def test_sender_kind(address, kind, monkeypatch):
    # example.net stands in for a personal domain such as the ones in PERSONAL_DOMAINS.
    monkeypatch.setattr(ms, "PERSONAL_DOMAINS", ms.PERSONAL_DOMAINS | {"example.net"})
    assert ms.sender_kind(address) == kind


def test_personal_domains_list_holds_the_common_free_mail_domains():
    assert {"gmail.com", "outlook.com", "hotmail.com", "yahoo.com", "icloud.com"} <= ms.PERSONAL_DOMAINS


@pytest.mark.parametrize("content,expected", [
    ("A", "A"), (" b.\n", "B"), ("A because", None), ("", None), (None, None), ("E", None),
])
def test_parse_answer_only_one_listed_letter(content, expected):
    assert ms.parse_answer(content, ["A", "B", "C", "D"]) == expected


def test_confidence_from_logprobs_reads_first_token():
    lp = {"content": [{"token": "A", "logprob": math.log(0.8),
                       "top_logprobs": [{"token": "A", "logprob": math.log(0.8)}, {"token": " B", "logprob": math.log(0.15)},
                                        {"token": "the", "logprob": math.log(0.05)}]}]}
    chosen, per = ms.confidence_from_logprobs(lp, ["A", "B"])
    assert chosen == pytest.approx(0.8)
    assert per == {"A": pytest.approx(0.8), "B": pytest.approx(0.15)}


def test_confidence_from_logprobs_missing_is_none():
    assert ms.confidence_from_logprobs(None, ["A"]) == (None, {})


def test_call_cost_uses_wingman_fast_prices():
    assert ms.call_cost({"prompt_tokens": 1_000_000, "completion_tokens": 1_000_000}) == pytest.approx(0.76)


def test_route_question_has_three_routes_plus_unsure():
    q = ms.load_questions()[1]
    assert q["labels"] == ["devthrottle/mail-desk", "citizenship", "notification", "unsure"]
    prompt = ms.render(q["template"], {"sender_name": "n", "sender_address": "a@example.org", "subject": "s", "preview": "x" * 500}, **q["extra"])
    assert "D. none of the above" in prompt and "x" * 201 not in prompt


def test_check_raw_dir_inside_repository_refused():
    with pytest.raises(SystemExit):
        ms.check_raw_dir(ms.HERE / "raw")


def _write_emails(raw_dir, n):
    import json
    emails = [{"key": f"{i:012x}", "received": "t", "sender_address": f"x{i}@example.com", "sender_name": "n",
               "subject": "s", "preview": "p"} for i in range(n)]
    (raw_dir / "emails.json").write_text(json.dumps(emails), encoding="utf-8")


def test_classify_spend_limit_stops_before_crossing(tmp_path, monkeypatch):
    import argparse, json
    _write_emails(tmp_path, 50)
    ms.cmd_classify(argparse.Namespace(raw_dir=str(tmp_path), endpoint="fake", key_env="UNUSED", spend_limit=0.0005, out=None))
    out = json.loads((tmp_path / "model_answers.fake.json").read_text())
    assert out["stopped"] and out["emails"] < 50
    assert out["spent_dollars"] <= 0.0005


def test_classify_fake_answers_every_email_with_both_questions(tmp_path):
    import argparse, json
    _write_emails(tmp_path, 5)
    ms.cmd_classify(argparse.Namespace(raw_dir=str(tmp_path), endpoint="fake", key_env="UNUSED", spend_limit=None, out=None))
    out = json.loads((tmp_path / "model_answers.fake.json").read_text())
    assert out["emails"] == 5 and out["stopped"] is None
    for row in out["rows"]:
        assert row["person_or_spam"]["label"] in ("person", "spam", "unsure")
        for q in ("person_or_spam", "route"):
            assert (row[q]["confidence"] is None) == (not row[q]["valid_answer"])


def test_classify_spend_limit_above_fifty_cents_is_capped(tmp_path):
    import argparse, json
    _write_emails(tmp_path, 1)
    ms.cmd_classify(argparse.Namespace(raw_dir=str(tmp_path), endpoint="fake", key_env="UNUSED", spend_limit=99.0, out=None))
    out = json.loads((tmp_path / "model_answers.fake.json").read_text())
    assert out["spend_limit_dollars"] == ms.SPEND_LIMIT


def test_classify_answer_outside_labels_becomes_unsure(tmp_path, monkeypatch):
    import argparse, json
    _write_emails(tmp_path, 1)
    monkeypatch.setattr(ms, "fake_completion", lambda prompt, letters: {
        "choices": [{"message": {"content": "Probably A"}, "logprobs": None}], "usage": {"prompt_tokens": 10, "completion_tokens": 2}})
    ms.cmd_classify(argparse.Namespace(raw_dir=str(tmp_path), endpoint="fake", key_env="UNUSED", spend_limit=None, out=None))
    row = json.loads((tmp_path / "model_answers.fake.json").read_text())["rows"][0]
    assert row["person_or_spam"]["label"] == "unsure" and row["route"]["label"] == "unsure"
    assert row["route"]["valid_answer"] is False and row["route"]["confidence"] is None


def test_synthetic_set_has_expected_labels_and_no_address():
    import json
    text = (ms.HERE / "synthetic" / "emails.json").read_text()
    assert "@" not in text
    items = json.loads(text)
    q = ms.load_questions()
    for it in items:
        assert it["expected"]["person_or_spam"] in q[0]["labels"] + ["unsure"]
        assert it["expected"]["route"] in q[1]["labels"]


if __name__ == "__main__":
    sys.exit(pytest.main([__file__, "-q", "-p", "no:cacheprovider"]))
