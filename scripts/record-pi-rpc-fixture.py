#!/usr/bin/env python3
"""Record real Pi wire logs (`pi --mode rpc`) as test fixtures for the Director's API-mode driver.

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 0. This replaces the
Architect's proof script in the issue's first comment. It drives a real Pi over its native
protocol and writes, per scenario, two files into the fixture folder:

  <scenario>.stdout.jsonl      Pi's standard output exactly as it arrived (after redaction, below).
                               Use it to test framing: records are split on line feed only.
  <scenario>.transcript.jsonl  Every record in both directions, in order, with timing:
                                 {"seq":0,"ms":0,"dir":"meta",...}            what was recorded, and how
                                 {"seq":n,"ms":t,"dir":"stdin","line":"..."}  a command we wrote
                                 {"seq":n,"ms":t,"dir":"stdout","line":"..."} a record Pi wrote
                                 {"seq":n,"ms":t,"dir":"stderr","line":"..."} a diagnostic line
                                 {"seq":n,"ms":t,"dir":"kill","signal":"SIGKILL"}  we ended Pi
                                 {"seq":n,"ms":t,"dir":"exit","code":0}       Pi's exit code
                               A fake process replays it: answer each stdin record with the stdout
                               records that follow it.

Redaction, applied to every recorded line (a plain text replacement inside JSON strings, so the
JSON stays valid): the work folder becomes /work (and "work" where Pi encodes it into a session
folder name), the user's home folder becomes /home/fixture, the model provider's base address
becomes https://model-provider.invalid, the model's display name is replaced (its id stays), and
each skill's description text is replaced (the skill names and locations stay). Nothing else
changes. The provider address and model name are learned before any scenario runs, from a
get_state-only Pi process that calls no model, so a run of only some scenarios is redacted too.
After every run the written files are checked for each real value, and the run fails if one is
left.

  python3 scripts/record-pi-rpc-fixture.py --redact-existing <fixture-dir> <work-dir>
applies the current redaction to fixtures already recorded (and checks them), without calling Pi's
model. It was used once, on 16 October 2026, to redact the model display name from the reviewed
recordings (phase 0 review, finding 3).

Spend: every scenario that calls the model ends with get_session_stats, and the summary printed
at the end lists the tokens per run for the mission's spend record. Pi reports cost 0 because the
configured model has no price, so the tokens are the only honest measure.

CC_* variables are removed from Pi's environment so Pi does not join the fleet as a session.

Usage:
  python3 scripts/record-pi-rpc-fixture.py <fixture-dir> <work-dir> [scenario ...]
Scenarios: plain tool abort resume refusal exit exit-before-ack (default: all, in that order; resume needs plain
in the same run, because it resumes plain's session by its id).
"""
import json
import os
import pathlib
import queue
import re
import signal
import subprocess
import sys
import threading
import time
import uuid

TIMEOUT_SECONDS = 180


SKILL_DESCRIPTION = re.compile(r"<description>[^<]*</description>")
JSON_DESCRIPTION = re.compile(r'"description":"(?:[^"\\]|\\.)*"')
REDACTED = "redacted by the fixture recorder"
MODEL_NAME_REDACTED = "model display name " + REDACTED
REDACTION_NOTE = ("work folder -> /work, home folder -> /home/fixture, "
                  "model base address -> https://model-provider.invalid, "
                  "skill descriptions -> " + REDACTED + ", model display name -> " + MODEL_NAME_REDACTED)
OLD_REDACTION_NOTE = ("work folder -> /work, home folder -> /home/fixture, "
                      "model base address -> https://model-provider.invalid, "
                      "skill descriptions -> " + REDACTED)


def pi_env() -> dict:
    # CC_* variables would make Pi join the fleet as a session.
    return {k: v for k, v in os.environ.items() if not k.startswith("CC_")}


class Redactor:
    def __init__(self, work_dir: pathlib.Path):
        # Pi names a project's session folder after the work folder with each "/" turned to "-".
        encoded_work = str(work_dir).lstrip("/").replace("/", "-")
        self.pairs = [(str(work_dir), "/work"), (encoded_work, "work"),
                      (str(pathlib.Path.home()), "/home/fixture")]
        self.learn_model(work_dir)

    def learn_model(self, work_dir: pathlib.Path):
        # A get_state-only process with no session: it calls no model and leaves no session file.
        out = subprocess.run(["pi", "--mode", "rpc", "--no-session"], cwd=work_dir, env=pi_env(),
                             input=b'{"id":"learn","type":"get_state"}\n', capture_output=True, timeout=60)
        state = next(json.loads(l) for l in out.stdout.splitlines() if b'"id":"learn"' in l)
        model = state["data"]["model"]
        # The display name is JSON-escaped here exactly as Pi writes it on the wire.
        self.pairs = [(model["baseUrl"], "https://model-provider.invalid"),
                      (json.dumps(model["name"])[1:-1], MODEL_NAME_REDACTED)] + self.pairs

    def real_values(self) -> list:
        return [real for real, _ in self.pairs]

    def apply(self, text: str) -> str:
        for real, fake in self.pairs:
            text = text.replace(real, fake)
        # The fleet's skill descriptions (in the system message and in get_commands) are not this
        # public repository's to publish. The skill names and locations stay: they show the skills
        # loaded in API mode.
        text = SKILL_DESCRIPTION.sub(f"<description>{REDACTED}</description>", text)
        if '"command":"get_commands"' in text:
            text = JSON_DESCRIPTION.sub(f'"description":"{REDACTED}"', text)
        return text


class PiRun:
    """One `pi --mode rpc` child process, with every record in both directions logged."""

    def __init__(self, name: str, fixture_dir: pathlib.Path, work_dir: pathlib.Path,
                 session_id: str, redactor: Redactor, purpose: str):
        self.name = name
        self.redactor = redactor
        self.start = time.monotonic()
        self.seq = 0
        self.lock = threading.Lock()
        self.events: "queue.Queue[dict]" = queue.Queue()
        self.counts: dict = {}
        self.stdout_raw = (fixture_dir / f"{name}.stdout.jsonl").open("wb")
        self.transcript = (fixture_dir / f"{name}.transcript.jsonl").open("w", encoding="utf-8", newline="\n")
        argv = ["pi", "--mode", "rpc", "--session-id", session_id]
        self._log({"dir": "meta", "scenario": name, "purpose": purpose,
                   "argv": argv, "piVersion": pi_version(),
                   "recorded": time.strftime("%Y-%m-%d"), "recorder": "scripts/record-pi-rpc-fixture.py",
                   "redaction": REDACTION_NOTE})
        self.proc = subprocess.Popen(argv, cwd=work_dir, env=pi_env(), stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()

    def _log(self, entry: dict):
        with self.lock:
            entry = {"seq": self.seq, "ms": int((time.monotonic() - self.start) * 1000), **entry}
            self.seq += 1
            self.transcript.write(json.dumps(entry, ensure_ascii=False) + "\n")
            self.transcript.flush()

    def _read_stdout(self):
        # Binary readline splits on LF only, which is the framing rule Pi's rpc.md requires.
        while line := self.proc.stdout.readline():
            text = self.redactor.apply(line.decode("utf-8").rstrip("\n"))
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                # Pi reserves standard output for JSON records; anything else is a protocol fault.
                # Keep it in the transcript and fail the scenario now, rather than time out later.
                self._log({"dir": "stdout", "line": text})
                self.events.put({"type": "_not_json", "line": text})
                continue
            self.stdout_raw.write(text.encode("utf-8") + b"\n")
            self.stdout_raw.flush()
            self._log({"dir": "stdout", "line": text})
            t = record.get("type")
            self.counts[t] = self.counts.get(t, 0) + 1
            self.events.put(record)
        self.events.put({"type": "_eof"})

    def _read_stderr(self):
        while line := self.proc.stderr.readline():
            self._log({"dir": "stderr", "line": self.redactor.apply(line.decode("utf-8", "replace").rstrip("\n"))})

    def send_raw(self, text: str):
        self._log({"dir": "stdin", "line": self.redactor.apply(text)})
        self.proc.stdin.write(text.encode("utf-8") + b"\n")
        self.proc.stdin.flush()

    def send(self, command: dict) -> str:
        command = {"id": "req-" + uuid.uuid4().hex[:8], **command}
        self.send_raw(json.dumps(command))
        return command["id"]

    def until(self, predicate, what: str) -> dict:
        deadline = time.monotonic() + TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            try:
                record = self.events.get(timeout=1)
            except queue.Empty:
                continue
            if record["type"] == "_eof":
                raise RuntimeError(f"[{self.name}] Pi exited while waiting for {what}")
            if record["type"] == "_not_json":
                raise RuntimeError(f"[{self.name}] Pi wrote a line that is not JSON: {record['line'][:200]}")
            if predicate(record):
                return record
        raise RuntimeError(f"[{self.name}] timed out waiting for {what}")

    def response(self, request_id: str) -> dict:
        return self.until(lambda r: r["type"] == "response" and r.get("id") == request_id,
                          f"response to {request_id}")

    def settled(self) -> dict:
        return self.until(lambda r: r["type"] == "agent_settled", "agent_settled")

    def request(self, command: dict) -> dict:
        return self.response(self.send(command))

    def kill(self):
        self._log({"dir": "kill", "signal": "SIGKILL"})
        self.proc.send_signal(signal.SIGKILL)

    def finish(self, close_stdin: bool = True) -> int:
        if close_stdin:
            self.proc.stdin.close()
        code = self.proc.wait(timeout=60)
        # Drain: the reader thread posts _eof once Pi's standard output closes.
        while self.events.get(timeout=30)["type"] != "_eof":
            pass
        time.sleep(0.2)  # let the stderr reader write its last line before the exit record
        self._log({"dir": "exit", "code": code})
        self.stdout_raw.close()
        self.transcript.close()
        return code


def pi_version() -> str:
    return subprocess.run(["pi", "--version"], capture_output=True, text=True).stdout.strip()


def tokens(run: PiRun) -> dict:
    return run.request({"type": "get_session_stats"})["data"]["tokens"]


def scenario_plain(ctx):
    run = PiRun("plain", ctx["fixtures"], ctx["work"], ctx["plain_session"], ctx["redactor"],
                "A plain prompt and its reply, then the read commands the dialect uses.")
    ack = run.request({"type": "prompt", "message": "Reply with exactly the word PONG"})
    assert ack["success"] and ack["data"]["disposition"] == "started", ack
    run.settled()
    reply = run.request({"type": "get_last_assistant_text"})["data"]["text"]
    run.request({"type": "get_state"})
    run.request({"type": "get_commands"})
    stats = tokens(run)
    return run, {"reply": reply, "tokens": stats}


def scenario_tool(ctx):
    run = PiRun("tool", ctx["fixtures"], ctx["work"], str(uuid.uuid4()), ctx["redactor"],
                "A prompt that runs the bash tool.")
    ack = run.request({"type": "prompt", "message":
                       "Use the bash tool to run: echo fixture-ok. Then reply with its output only."})
    assert ack["success"], ack
    run.settled()
    reply = run.request({"type": "get_last_assistant_text"})["data"]["text"]
    return run, {"reply": reply, "tokens": tokens(run)}


def scenario_abort(ctx):
    run = PiRun("abort", ctx["fixtures"], ctx["work"], str(uuid.uuid4()), ctx["redactor"],
                "A long prompt stopped with abort once the reply is streaming.")
    ack = run.request({"type": "prompt", "message": "Write a 2000 word essay about rivers."})
    assert ack["success"], ack
    run.until(lambda r: r["type"] == "message_update"
              and r["assistantMessageEvent"]["type"] in ("text_delta", "thinking_delta"), "first delta")
    abort_id = run.send({"type": "abort"})
    seen = {}

    def note(r):
        if r["type"] == "agent_settled":
            seen["settled"] = r
        return r["type"] == "response" and r.get("id") == abort_id

    abort_response = run.until(note, "abort response")
    if "settled" not in seen:
        seen["settled"] = run.settled()
    run.request({"type": "get_last_assistant_text"})
    return run, {"abort": abort_response, "settled": seen["settled"], "tokens": tokens(run)}


def scenario_resume(ctx):
    run = PiRun("resume", ctx["fixtures"], ctx["work"], ctx["plain_session"], ctx["redactor"],
                "A new Pi process resuming the plain scenario's session by its id.")
    ack = run.request({"type": "prompt", "message":
                       "What single word did you reply with in your previous message? Reply with that word only."})
    assert ack["success"], ack
    run.settled()
    reply = run.request({"type": "get_last_assistant_text"})["data"]["text"]
    state = run.request({"type": "get_state"})["data"]
    return run, {"reply": reply, "messageCount": state["messageCount"], "tokens": tokens(run)}


def scenario_refusal(ctx):
    run = PiRun("refusal", ctx["fixtures"], ctx["work"], str(uuid.uuid4()), ctx["redactor"],
                "Real refusals: a prompt while streaming with no streamingBehavior, an unknown "
                "command, malformed JSON; then a prompt Pi queues (followUp), and abort while idle.")
    ack = run.request({"type": "prompt", "message": "Count from 1 to 30, one number per line, nothing else."})
    assert ack["success"], ack
    run.until(lambda r: r["type"] == "message_update", "first message_update")
    refused = run.request({"type": "prompt", "message": "Reply with exactly the word PONG"})
    unknown = run.request({"type": "no_such_command"})
    run.send_raw("{this is not json")
    parse = run.until(lambda r: r["type"] == "response" and r.get("command") == "parse", "parse error")
    queued = run.request({"type": "prompt", "message": "Reply with exactly the word DONE",
                          "streamingBehavior": "followUp"})
    run.settled()
    state = run.request({"type": "get_state"})["data"]
    while state["isStreaming"] or state["pendingMessageCount"]:
        run.settled()
        state = run.request({"type": "get_state"})["data"]
    idle_abort = run.request({"type": "abort"})
    reply = run.request({"type": "get_last_assistant_text"})["data"]["text"]
    return run, {"refused": refused, "unknown": unknown, "parse": parse, "queued": queued,
                 "idleAbort": idle_abort, "reply": reply, "tokens": tokens(run)}


def scenario_exit(ctx):
    session_id = str(uuid.uuid4())
    run = PiRun("exit", ctx["fixtures"], ctx["work"], session_id, ctx["redactor"],
                "Pi ended mid-turn (SIGKILL by the recorder): the stream ends with no agent_settled.")
    ack = run.request({"type": "prompt", "message": "Write a 2000 word essay about rivers."})
    assert ack["success"], ack
    run.until(lambda r: r["type"] == "message_update"
              and r["assistantMessageEvent"]["type"] in ("text_delta", "thinking_delta"), "first delta")
    run.kill()
    code = run.finish(close_stdin=False)
    # Read the session's stats from a second process (no model call) for the spend record. This
    # second process is not a fixture.
    out = subprocess.run(["pi", "--mode", "rpc", "--session-id", session_id], cwd=ctx["work"], env=pi_env(),
                         input=b'{"id":"s","type":"get_session_stats"}\n', capture_output=True, timeout=60)
    stats = next(json.loads(l) for l in out.stdout.splitlines() if b'"id":"s"' in l)["data"]["tokens"]
    return None, {"exitCode": code, "tokens": stats}


def scenario_exit_before_ack(ctx):
    run = PiRun("exit-before-ack", ctx["fixtures"], ctx["work"], str(uuid.uuid4()), ctx["redactor"],
                "Pi ended (SIGKILL by the recorder) right after the prompt was written, before its "
                "acknowledgement: the stream ends with no response to the prompt.")
    run.send({"type": "prompt", "message": "Reply with exactly the word PONG"})
    # Pi takes about 0.3 seconds to start and answer; end it well inside that window, before it
    # can reach the model.
    time.sleep(0.05)
    run.kill()
    code = run.finish(close_stdin=False)
    acknowledged = run.counts.get("response", 0)
    if acknowledged:
        raise RuntimeError(f"[exit-before-ack] Pi answered {acknowledged} time(s) before it was killed; "
                           "the scenario did not happen - record it again")
    return None, {"exitCode": code, "events": run.counts,
                  "tokens": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "total": 0}}


SCENARIOS = {"plain": scenario_plain, "tool": scenario_tool, "abort": scenario_abort,
             "resume": scenario_resume, "refusal": scenario_refusal, "exit": scenario_exit,
             "exit-before-ack": scenario_exit_before_ack}


def check_redacted(fixtures: pathlib.Path, redactor: Redactor):
    """Fail if any real value the redaction replaces is still in a fixture file."""
    leaks = [(f.name, real) for f in sorted(fixtures.glob("*.jsonl"))
             for real in redactor.real_values() if real in f.read_text(encoding="utf-8")]
    if leaks:
        raise SystemExit(f"redaction check FAILED, real values left in fixtures: {leaks}")
    print(f"redaction check passed: {len(list(fixtures.glob('*.jsonl')))} files, "
          f"{len(redactor.real_values())} real values absent")


def redact_existing(fixtures: pathlib.Path, work: pathlib.Path):
    redactor = Redactor(work)
    for f in sorted(fixtures.glob("*.jsonl")):
        raw = f.read_bytes()
        # Line by line, exactly as recording applies it: some rules depend on the record they are in.
        lines = raw.decode("utf-8").split("\n")
        text = "\n".join(redactor.apply(line) for line in lines)
        text = text.replace(OLD_REDACTION_NOTE + '"', REDACTION_NOTE + '"')
        if text.encode("utf-8") != raw:
            f.write_bytes(text.encode("utf-8"))
            print(f"redacted {f.name}")
    check_redacted(fixtures, redactor)


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "--redact-existing":
        work = pathlib.Path(sys.argv[3]).resolve()
        work.mkdir(parents=True, exist_ok=True)
        redact_existing(pathlib.Path(sys.argv[2]).resolve(), work)
        return
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    fixtures = pathlib.Path(sys.argv[1]).resolve()
    work = pathlib.Path(sys.argv[2]).resolve()
    names = sys.argv[3:] or list(SCENARIOS)
    unknown = [n for n in names if n not in SCENARIOS]
    if unknown:
        raise SystemExit(f"unknown scenario(s): {unknown}; known: {list(SCENARIOS)}")
    if "resume" in names and "plain" not in names:
        raise SystemExit("resume needs plain in the same run")
    fixtures.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True, exist_ok=True)
    ctx = {"fixtures": fixtures, "work": work, "redactor": Redactor(work),
           "plain_session": str(uuid.uuid4())}
    summary = {}
    for name in names:
        print(f"== {name}", flush=True)
        run, result = SCENARIOS[name](ctx)
        if run is not None:
            result["exitCode"] = run.finish()
            result["events"] = run.counts
        summary[name] = result
        print(json.dumps(result, indent=1)[:2000], flush=True)
    check_redacted(fixtures, ctx["redactor"])
    print("SUMMARY " + json.dumps({n: {"tokens": s["tokens"], "exitCode": s["exitCode"]}
                                   for n, s in summary.items()}))


if __name__ == "__main__":
    main()
