"""Unit tests for cc_shared.gateway - the ONE door the command line uses to reach the fleet.

Remove-the-network-port mission, phase 2. These were the Director-API helpers' tests; the helpers now
call the Gateway with this session's own key and there is no local path, so the transport cases below
are re-pinned against that. The pure resolution helpers are unchanged and their tests with them - what
they do never depended on which server answered.
"""

import io
import json
import sys
import urllib.error
from pathlib import Path

# Make cc_shared importable when tests run from the tools/ tree.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

import pytest  # noqa: E402

from cc_shared import gateway  # noqa: E402


def _session(sid, name=None, machine="machine-A", number=None):
    s = {"sessionId": sid, "name": name, "machineName": machine}
    if number is not None:
        s["number"] = number
    return s


SESSIONS = [
    _session("4c810000-1111-2222-3333-444444444444", "feature-work", number=412),
    _session("9b2f0000-aaaa-bbbb-cccc-dddddddddddd", "docs", number=305),
    _session("9b2f9999-eeee-ffff-0000-111111111111", "docs-helper", number=777),
]


def test_short_id_truncates_to_eight():
    assert gateway.short_id("4c810000-1111") == "4c810000"
    assert gateway.short_id("abc") == "abc"


def test_field_tolerates_camel_and_pascal_case():
    assert gateway.field({"sessionId": "x"}, "sessionId", "SessionId") == "x"
    assert gateway.field({"SessionId": "y"}, "sessionId", "SessionId") == "y"
    assert gateway.field({}, "name", "Name", default="-") == "-"


def test_resolve_target_exact_full_id_wins():
    full = "9b2f0000-aaaa-bbbb-cccc-dddddddddddd"
    matches = gateway.resolve_target(SESSIONS, full)
    assert len(matches) == 1
    assert gateway.field(matches[0], "sessionId", "SessionId") == full


def test_resolve_target_unique_prefix_matches_one():
    matches = gateway.resolve_target(SESSIONS, "4c81")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_ambiguous_prefix_returns_all_candidates():
    matches = gateway.resolve_target(SESSIONS, "9b2f")
    assert len(matches) == 2  # caller must refuse and list these


def test_resolve_target_by_exact_name():
    matches = gateway.resolve_target(SESSIONS, "docs")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "docs"


def test_resolve_target_no_match_returns_empty():
    assert gateway.resolve_target(SESSIONS, "zzzz") == []


# --- Issue #821: address a session by its three-digit number ---------------------------------


def test_resolve_target_by_three_digit_number():
    matches = gateway.resolve_target(SESSIONS, "412")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_number_selects_exact_session():
    matches = gateway.resolve_target(SESSIONS, "305")
    assert len(matches) == 1
    assert gateway.field(matches[0], "sessionId", "SessionId") == "9b2f0000-aaaa-bbbb-cccc-dddddddddddd"


def test_resolve_target_unused_number_returns_empty():
    # A three-digit token no active session holds yields the standard no-match (empty) result,
    # not a crash and not a wrong-session match.
    assert gateway.resolve_target(SESSIONS, "999") == []


def test_resolve_target_number_takes_precedence_over_id_prefix():
    # "412" is the number of feature-work; even if it coincided with an id prefix, the number wins.
    matches = gateway.resolve_target(SESSIONS, "412")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_three_digit_falls_back_to_id_prefix_when_no_number():
    # A three-digit token that no session holds as a number still resolves by id prefix.
    sessions = [_session("305abcde-1111-2222-3333-444444444444", "by-id-prefix", number=601)]
    matches = gateway.resolve_target(sessions, "305")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "by-id-prefix"


def test_resolve_target_id_prefix_unchanged_with_numbers_present():
    # Regression: id-prefix addressing still works when sessions carry numbers.
    matches = gateway.resolve_target(SESSIONS, "4c81")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_name_unchanged_with_numbers_present():
    # Regression: exact-name addressing still works when sessions carry numbers.
    matches = gateway.resolve_target(SESSIONS, "docs")
    assert len(matches) == 1
    assert gateway.field(matches[0], "name", "Name") == "docs"



# --- The server's sentence must reach the user, not the status code ---------------------------
#
# A refusal written by the server - naming the agent, the command tried, and the two ways to fix it -
# travels across HTTP intact and is then thrown away if the extractor reads only "error", because an
# unhandled exception answers in ASP.NET problem-details, which carries the sentence in "detail". Both
# shapes are pinned here so neither can be traded for the other, and the last tests drive the real
# raise site rather than the helper alone - a helper that returns the right string proves nothing if
# nobody calls it.

SPAWN_SENTENCE = (
    'Claude Code could not be started: the command "definitely-not-an-agent-cli-9f3a" is not a '
    "file on that machine and was not found on its PATH. Set this agent's executable "
    "path in Settings, Agents - or install Claude Code - and start the session again."
)
PROBLEM_DETAILS = json.dumps({
    "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    "title": "An error occurred while processing your request.",
    "status": 500,
    "detail": SPAWN_SENTENCE,
})


def test_error_message_reads_problem_details_detail():
    assert gateway._error_message(PROBLEM_DETAILS, 500) == SPAWN_SENTENCE


def test_error_message_still_reads_the_servers_own_error_shape():
    # The two shapes coexist in this API; fixing one must not cost the other.
    body = json.dumps({"error": "text is required"})
    assert gateway._error_message(body, 400) == "text is required"


def test_error_message_prefers_error_over_detail_when_a_body_carries_both():
    body = json.dumps({"error": "the specific one", "detail": "the generic one"})
    assert gateway._error_message(body, 500) == "the specific one"


def test_error_message_accepts_pascal_case_detail():
    assert gateway._error_message(json.dumps({"Detail": "a sentence"}), 500) == "a sentence"


def test_error_message_shows_a_generic_title_beside_the_code_not_instead_of_it():
    # problem-details "title" says less than the status code, so promoting it would make the
    # message worse. It is additive only.
    body = json.dumps({"title": "Not Found", "status": 404})
    assert gateway._error_message(body, 404) == "HTTP 404 from the Gateway: Not Found"


@pytest.mark.parametrize("body", [
    "",
    "<html>a proxy error page</html>",
    json.dumps(["not", "an", "object"]),
    json.dumps({"status": 500}),
    json.dumps({"error": "   ", "detail": ""}),  # present but empty: not a sentence
])
def test_error_message_falls_back_to_the_status_when_the_body_carries_no_sentence(body):
    assert gateway._error_message(body, 500) == "HTTP 500 from the Gateway"


def test_error_message_blames_the_machine_when_the_gateway_says_the_machine_failed():
    """The status code alone cannot carry this difference, so the Gateway stamps a header.

    A 502 from an edge proxy in front of a dead Gateway and a 502 the Gateway itself composed about a
    Director that went quiet are the same number and opposite facts. Told the first when it is the
    second, an agent goes and debugs the Gateway while the machine it actually wanted sits there
    unreachable.
    """
    message = gateway._error_message("", 502, fault_is_director=True)
    assert "the machine that owns it" in message
    assert "the Gateway answered" in message


# A refused session key (issues #2457, #2459). The Gateway's own sentence asserts the credential is
# the fault; on 2026-08-05 that was wrong for every session in the fleet at once, because the hosted
# Gateway predated the session key registry entirely.

def test_a_401_keeps_the_servers_sentence_and_admits_an_out_of_date_gateway():
    body = json.dumps({"error": "missing or invalid token"})
    message = gateway._error_message(body, 401)

    # The server's own words survive - this must ADD to the answer, never replace it.
    assert message.startswith("missing or invalid token")
    # ...and the cause the server cannot know about is named, with the line that tells them apart.
    assert "OLDER than the Director" in message
    assert "session key re-registration incomplete (older Gateway?)" in message


def test_a_401_does_not_assert_which_of_the_two_causes_it_is():
    """The tool cannot tell from here, and a confident wrong verdict is what cost the morning.

    Both causes must be offered as possibilities. A message that concluded "your key expired" would
    reproduce the original defect with different words.
    """
    message = gateway._error_message(json.dumps({"error": "missing or invalid token"}), 401)
    assert "unknown or expired" in message   # cause 2 is still on the table
    assert "the key is fine" in message      # cause 1 is too


def test_a_401_with_no_body_still_carries_the_causes():
    # An edge proxy can refuse before the Gateway is reached, so there may be no sentence at all.
    message = gateway._error_message("", 401)
    assert message.startswith("HTTP 401 from the Gateway")
    assert "OLDER than the Director" in message


@pytest.mark.parametrize("code", [400, 403, 404, 500, 502])
def test_only_a_401_carries_the_refused_key_causes(code):
    """Scoped to the one status it explains.

    403 is the near miss that makes this worth a test: a session key presented to a route outside
    its scope is refused with 403, and it is NOT this problem. Pasting the session-key story onto
    every failure would bury the sentence that does apply.
    """
    message = gateway._error_message(json.dumps({"error": "nope"}), code)
    assert "OLDER than the Director" not in message


def _http_error(code: int, body: str, headers=None) -> urllib.error.HTTPError:
    return urllib.error.HTTPError(
        url="http://gw.example/sessions", code=code, msg="Internal Server Error",
        hdrs=headers, fp=io.BytesIO(body.encode("utf-8")),
    )


def _session_env(monkeypatch):
    """A session that has been given both halves of its Gateway credential."""
    monkeypatch.setenv("CC_GATEWAY_URL", "http://gw.example")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")


def test_request_surfaces_the_sentence_from_a_problem_details_response(monkeypatch):
    """End to end through _request: this is the path every cc-devthrottle command takes."""
    _session_env(monkeypatch)
    monkeypatch.setattr(gateway._OPENER, "open",
                        lambda *a, **k: (_ for _ in ()).throw(_http_error(500, PROBLEM_DETAILS)))

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.post_json("machines/m/sessions", {"repoPath": "C:/x", "agent": "ClaudeCode"})

    assert str(caught.value) == SPAWN_SENTENCE
    assert "HTTP 500" not in str(caught.value)


def test_request_still_reports_the_status_when_the_body_carries_no_sentence(monkeypatch):
    _session_env(monkeypatch)
    monkeypatch.setattr(gateway._OPENER, "open",
                        lambda *a, **k: (_ for _ in ()).throw(_http_error(502, "")))

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.get_json("sessions")

    assert str(caught.value) == "HTTP 502 from the Gateway"


def test_request_presents_the_session_key_as_a_bearer_token(monkeypatch):
    """The credential on the wire is THIS SESSION's key - not the machine secret, which is gone."""
    seen = {}

    class _Resp:
        def read(self):
            return b"[]"

        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

    def _capture(req, timeout=None):
        seen["auth"] = req.get_header("Authorization")
        seen["url"] = req.full_url
        return _Resp()

    _session_env(monkeypatch)
    monkeypatch.setattr(gateway._OPENER, "open", _capture)
    gateway.get_json("sessions?envelope=true")

    assert seen["auth"] == "Bearer a-session-key"
    assert seen["url"] == "http://gw.example/sessions?envelope=true"


# --- No Gateway means no agent tooling, and the message has to say so -------------------------
#
# This is the cost the mission accepts, so the failure is the user-facing part of the design rather
# than an edge case. Each half is checked separately: a session holding one without the other is a
# bug in the stamping, and one collapsed message would hide which half went missing.


def test_a_session_with_no_gateway_address_is_told_to_install_the_self_hosted_gateway(monkeypatch):
    monkeypatch.delenv("CC_GATEWAY_URL", raising=False)
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.get_json("sessions")

    message = str(caught.value)
    assert "CC_GATEWAY_URL is not set" in message
    assert "self-hosted gateway" in message


def test_a_session_with_no_gateway_key_says_so_separately(monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "http://gw.example")
    monkeypatch.delenv("CC_GATEWAY_SESSION_KEY", raising=False)

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.get_json("sessions")

    message = str(caught.value)
    assert "CC_GATEWAY_SESSION_KEY is not set" in message
    assert "self-hosted gateway" in message


def test_an_unreachable_gateway_says_there_is_no_local_path(monkeypatch):
    """THE NO-FALLBACK RULE, as the user experiences it.

    An unreachable Gateway must not read as a transient blip the tool might route around, because
    there is nothing to route around to. The sentence says the fleet commands go through it and there
    is no local path - so nobody goes looking for the second door this mission removed.
    """
    _session_env(monkeypatch)
    monkeypatch.setattr(gateway._OPENER, "open",
                        lambda *a, **k: (_ for _ in ()).throw(urllib.error.URLError("connection refused")))

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.get_json("sessions")

    message = str(caught.value)
    assert "Cannot reach the Gateway at http://gw.example" in message
    assert "no local path" in message


# --- The roster envelope, and what a silent Gateway must NOT be read as -----------------------


def test_get_fleet_reads_the_gateways_folded_verdicts(monkeypatch):
    envelope = {
        "sessions": [{"sessionId": "a"}],
        "rosterComplete": False,
        "rosterIncompleteReason": "1 Director could not be reached",
        "rosterStaleAnswerCaution": "1 machine is connected but has not reported recently",
    }
    monkeypatch.setattr(gateway, "get_json", lambda path, **k: envelope)

    sessions, complete, reason, stale = gateway.get_fleet()
    assert sessions == [{"sessionId": "a"}]
    assert complete is False
    assert reason == "1 Director could not be reached"
    assert stale.startswith("1 machine is connected")


def test_a_gateway_that_says_nothing_about_completeness_is_UNKNOWN_not_complete(monkeypatch):
    """None is not True, and coalescing it would rebuild the defect one layer up.

    A Gateway that predates this field serves the bare array. Reading that silence as a guarantee is
    exactly the failure the completeness verdict exists to close: absent reading identical to empty.
    """
    monkeypatch.setattr(gateway, "get_json", lambda path, **k: [{"sessionId": "a"}])

    sessions, complete, reason, stale = gateway.get_fleet()
    assert sessions == [{"sessionId": "a"}]
    assert complete is None
    assert reason is None and stale is None
    assert gateway.roster_caveat(complete, reason) != ""  # and it says so out loud


# --- The credential must not cross an origin boundary -----------------------------------------


def _redirect_rig():
    """Two real loopback servers: the first redirects to the second, which records what it got.

    Driven with real HTTP rather than a mocked opener on purpose. The defect was in Python's own
    redirect handling - the default handler copies Authorization onto the redirected request - so a
    test that faked the redirect would have tested the fake and not the behaviour that leaked.
    """
    import http.server
    import threading

    received = {}

    class _Second(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            received["auth"] = self.headers.get("Authorization")
            body = b"{}"
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    second = http.server.HTTPServer(("127.0.0.1", 0), _Second)
    threading.Thread(target=second.serve_forever, daemon=True).start()
    second_url = "http://127.0.0.1:%d" % second.server_address[1]

    class _First(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(302)
            self.send_header("Location", second_url + "/sessions")
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, *a):
            pass

    first = http.server.HTTPServer(("127.0.0.1", 0), _First)
    threading.Thread(target=first.serve_forever, daemon=True).start()
    first_url = "http://127.0.0.1:%d" % first.server_address[1]

    return first_url, received, first, second


def test_a_cross_origin_redirect_never_receives_the_session_key(monkeypatch):
    """The proved disclosure path: a redirect to another origin must not carry the credential.

    Python's default opener copies Authorization onto the redirected request even when the host and
    port change, so any redirect - from the Gateway, a reverse proxy, or a hijacked route - handed
    this session's key to whoever the Location named.
    """
    first_url, received, first, second = _redirect_rig()
    try:
        monkeypatch.setenv("CC_GATEWAY_URL", first_url)
        monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

        with pytest.raises(gateway.GatewayError) as caught:
            gateway.get_json("sessions")

        # Refused, and the sentence says why rather than surfacing a redirect loop or a bare 302.
        assert "different origin" in str(caught.value)
        # And - the point of the whole finding - the other origin never saw the credential.
        assert received.get("auth") is None
    finally:
        first.shutdown()
        second.shutdown()


def test_a_same_origin_redirect_is_still_followed(monkeypatch):
    """The guard must not break ordinary redirects back to the Gateway itself."""
    import http.server
    import threading

    state = {}

    class _Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/sessions":
                self.send_response(302)
                self.send_header("Location", "/sessions-moved")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            state["auth"] = self.headers.get("Authorization")
            body = b'{"ok": true}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    srv = http.server.HTTPServer(("127.0.0.1", 0), _Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    try:
        monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:%d" % srv.server_address[1])
        monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

        assert gateway.get_json("sessions") == {"ok": True}
        assert state["auth"] == "Bearer a-session-key"
    finally:
        srv.shutdown()


# --- The status travels with the failure -------------------------------------------------------
#
# Why this is worth its own pair of tests: a caller that cannot tell a REFUSAL from a LOST REPLY has
# to describe both the same way, and `session stop` was describing a timeout - which can arrive after
# the session was already ended - with the words "Not stopped:". The status is the only thing that
# tells them apart, so these prove it survives the trip out of urllib and onto the error, and that it
# is None where there was never a status to have.


def test_an_http_failure_carries_the_status_it_was_refused_with(monkeypatch):
    """Over a real server, because err.code comes out of urllib's own HTTPError, not out of us."""
    import http.server
    import threading

    class _Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            body = b'{"error": "a reason is required to stop a session"}'
            self.send_response(400)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    srv = http.server.HTTPServer(("127.0.0.1", 0), _Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    try:
        monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:%d" % srv.server_address[1])
        monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

        with pytest.raises(gateway.GatewayError) as caught:
            gateway.post_json("sessions/9c41e7a2/stop", {"reason": ""})

        assert caught.value.status == 400
        # And the server's sentence is still the message - the status is carried BESIDE it, never
        # instead of it.
        assert "a reason is required to stop a session" in str(caught.value)
    finally:
        srv.shutdown()


def test_a_failure_that_never_got_an_answer_has_no_status_at_all(monkeypatch):
    """None is the honest value, and it is what puts a lost reply on the "we do not know" side.

    A default of, say, 0 or 500 would have made every unanswered request look like an answer, which
    is the whole failure this attribute exists to prevent.
    """
    import socket

    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    listener.close()                    # nothing is listening now: the connection is refused

    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:%d" % port)
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.post_json("sessions/9c41e7a2/stop", {"reason": "why"}, timeout=2)

    assert caught.value.status is None


# --- One caller-typed value stays ONE path segment ----------------------------------------------


def test_path_segment_escapes_the_separator_so_a_name_stays_one_segment():
    """The defect (inspection 1, finding I6): a session named "Mission / Worker" was interpolated raw
    into "sessions/{target}/stop", which is a different route with a different number of segments."""
    assert gateway.path_segment("Mission / Worker") == "Mission%20%2F%20Worker"
    # ? and # stop being part of the target and start being URL syntax, so they go too.
    assert gateway.path_segment("what?why#now") == "what%3Fwhy%23now"
    # An ordinary identifier passes through untouched: nothing about this changes the common case.
    assert gateway.path_segment("9c41e7a2-1111-2222-3333-444455556666") == (
        "9c41e7a2-1111-2222-3333-444455556666"
    )


# --- Transport failures the user must read as a sentence, not a traceback ----------------------


def test_an_unanswered_request_is_a_sentence_not_a_bare_timeout(monkeypatch):
    """A read timeout escapes urlopen as TimeoutError, NOT URLError, so it slipped past the catch."""
    import socket
    import threading
    import time

    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)

    def _accept_and_stall():
        try:
            conn, _ = listener.accept()
            time.sleep(5)
            conn.close()
        except OSError:
            pass

    threading.Thread(target=_accept_and_stall, daemon=True).start()
    try:
        monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:%d" % listener.getsockname()[1])
        monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

        with pytest.raises(gateway.GatewayError) as caught:
            gateway.get_json("sessions", timeout=0.4)

        assert "did not answer in time" in str(caught.value)
    finally:
        listener.close()


def test_a_misconfigured_gateway_url_is_a_sentence_not_a_valueerror(monkeypatch):
    """An invalid CC_GATEWAY_URL raises ValueError out of urlopen before any request is made."""
    monkeypatch.setenv("CC_GATEWAY_URL", "not-a-url")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

    with pytest.raises(gateway.GatewayError) as caught:
        gateway.get_json("sessions")

    assert "CC_GATEWAY_URL" in str(caught.value)


def test_a_200_that_is_not_json_is_a_sentence_not_a_decode_error(monkeypatch):
    """json.loads sat outside any decoding catch, so a non-JSON 200 reached the user as a traceback."""
    import http.server
    import threading

    class _Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            body = b"<html>not json</html>"
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *a):
            pass

    srv = http.server.HTTPServer(("127.0.0.1", 0), _Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    try:
        monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:%d" % srv.server_address[1])
        monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "a-session-key")

        with pytest.raises(gateway.GatewayError) as caught:
            gateway.get_json("sessions")

        assert "usable answer" in str(caught.value)
    finally:
        srv.shutdown()


# --- parse_json_body: the shared 2xx guard for the requests-based clients (issue #2486) ----------


class _SuccessResponse:
    """Duck-typed stand-in for requests.Response - exactly the shape parse_json_body reads."""

    def __init__(self, status_code=200, content=b"", content_type=None, parsed=None):
        self.status_code = status_code
        self.content = content
        self.headers = {"Content-Type": content_type} if content_type else {}
        self._parsed = parsed

    def json(self):
        if self._parsed is None:
            raise ValueError("Expecting value: line 1 column 1 (char 0)")
        return self._parsed


def test_parse_json_body_returns_the_parsed_json():
    resp = _SuccessResponse(
        content=b'{"a": 1}', content_type="application/json; charset=utf-8", parsed={"a": 1}
    )
    assert gateway.parse_json_body(resp, "http://gw") == {"a": 1}


def test_parse_json_body_treats_an_empty_body_as_an_empty_object():
    assert gateway.parse_json_body(_SuccessResponse(content=b""), "http://gw") == {}


def test_parse_json_body_refuses_the_web_app_shell_in_a_sentence():
    resp = _SuccessResponse(content=b"<!doctype html>", content_type="text/html; charset=utf-8")
    with pytest.raises(gateway.GatewayError) as caught:
        gateway.parse_json_body(resp, "http://gw")
    assert "fell through to the Gateway's web app" in str(caught.value)


def test_parse_json_body_refuses_unparseable_json_in_a_sentence():
    resp = _SuccessResponse(content=b"{ truncated", content_type="application/json")
    with pytest.raises(gateway.GatewayError) as caught:
        gateway.parse_json_body(resp, "http://gw")
    assert "could not be parsed" in str(caught.value)


def test_parse_json_body_raises_the_callers_own_error_class():
    """The email and diagnostics clients keep their own GatewayError classes; the guard must raise
    THEIRS so their except handlers still catch it - the aliasing trap workflow_ops.py documents."""

    class LocalGatewayError(Exception):
        pass

    resp = _SuccessResponse(content=b"<!doctype html>", content_type="text/html")
    with pytest.raises(LocalGatewayError):
        gateway.parse_json_body(resp, "http://gw", LocalGatewayError)
