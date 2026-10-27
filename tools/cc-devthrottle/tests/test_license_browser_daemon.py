"""A license run never breaks a session that is driving the same browser profile (license_signin, browser_ops).

The daily approver check reads every browser profile on the machine, and sessions and night-time factories drive those
profiles. A profile that is already running is read through that running browser, under a browser-harness daemon of the
run's own: the profile's own daemon is never used and never reloaded, and the browser is never closed. A profile that
was stopped is started, read, and stopped again. browser-harness and the Gateway are faked: nothing here starts a
process or reaches a browser.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import browser_ops, license_signin  # noqa: E402

PROFILE_ENV = {"BU_NAME": "work-chrome", "BU_CDP_URL": "http://work-chrome.attach.example"}


class _Harness:
    """Stands in for subprocess.run of browser-harness: records each call's arguments and daemon name."""

    def __init__(self):
        self.calls = []

    def __call__(self, args, **kwargs):
        self.calls.append((args[1:], kwargs["env"]["BU_NAME"], kwargs["env"]["BU_CDP_URL"]))
        return subprocess.CompletedProcess(args, 0, stdout='RESULT {"state": "signed-out"}\n', stderr="")


def _fake_harness(monkeypatch) -> _Harness:
    harness = _Harness()
    monkeypatch.setattr(license_signin.shutil, "which", lambda name: "browser-harness")
    monkeypatch.setattr(license_signin.subprocess, "run", harness)
    return harness


def test_runHarness_AProfileARunningSessionDrives_NeverUsesOrReloadsTheProfilesOwnDaemon(monkeypatch):
    harness = _fake_harness(monkeypatch)

    license_signin._run_harness("print(1)", dict(PROFILE_ENV), 30)

    own = license_signin.run_daemon_name("work-chrome")
    assert own != "work-chrome"
    assert harness.calls == [
        ([], own, "http://work-chrome.attach.example"),
        (["--reload"], own, "http://work-chrome.attach.example"),
    ]
    assert all(name != "work-chrome" for _, name, _ in harness.calls)


# browser-harness takes a daemon name of [A-Za-z0-9_-]{1,64} and refuses anything else ("invalid BU_NAME").
_DAEMON_NAME = re.compile(r"[A-Za-z0-9_-]{1,64}")


@pytest.mark.parametrize("length", [1, 18, 49, 50, 51, 64])
@pytest.mark.parametrize("pid", [7, 99999, 4294967295])
def test_runDaemonName_EveryProfileNameBrowserHarnessTakes_GivesANameItTakesToo(length, pid):
    profile = ("p" * length)[:length]

    name = license_signin.run_daemon_name(profile, pid)

    assert _DAEMON_NAME.fullmatch(name), (len(name), name)
    assert name != profile and name.endswith(f"-license-{pid}")


def test_runDaemonName_AShortProfileName_IsKeptWholeSoTheDaemonIsReadable():
    assert license_signin.run_daemon_name("work-chrome", 31648) == "work-chrome-license-31648"


def test_runDaemonName_TwoLongProfileNamesThatShareABeginning_StayApart():
    first = "a-very-long-browser-profile-name-the-owner-typed-for-the-factory-1"[:64]
    second = first[:-1] + "2"

    assert license_signin.run_daemon_name(first, 99999) != license_signin.run_daemon_name(second, 99999)


def test_runHarness_TheBrowserStepFails_StillEndsOnlyItsOwnDaemon(monkeypatch):
    harness = _fake_harness(monkeypatch)

    def failing(args, **kwargs):
        harness(args, **kwargs)
        if args[1:] == []:
            raise subprocess.TimeoutExpired(args, 30)
        return subprocess.CompletedProcess(args, 0, stdout="", stderr="")

    monkeypatch.setattr(license_signin.subprocess, "run", failing)

    try:
        license_signin._run_harness("print(1)", dict(PROFILE_ENV), 30)
    except license_signin.SignInError:
        pass

    assert [(args, name) for args, name, _ in harness.calls] == [
        ([], license_signin.run_daemon_name("work-chrome")),
        (["--reload"], license_signin.run_daemon_name("work-chrome")),
    ]


def _fake_gateway(monkeypatch, status: str) -> list:
    posted = []
    monkeypatch.setattr(browser_ops, "_browsers", lambda: [{"id": "work-chrome", "name": "Work Chrome", "status": status}])
    monkeypatch.setattr(browser_ops, "_mine", lambda: "directors/dir-a/browsers")

    def post_json(path, body):
        posted.append(path)
        return {"buName": "work-chrome", "buCdpUrl": "http://work-chrome.attach.example"}

    monkeypatch.setattr(browser_ops.gateway, "post_json", post_json)
    return posted


def test_drivenBrowser_AProfileAlreadyRunning_IsReadWhereItIs_AndNeverStopped(monkeypatch):
    posted = _fake_gateway(monkeypatch, "Ready")

    browser = browser_ops.DrivenBrowser("Work Chrome")
    browser.stop()

    # The start of a running profile is the Gateway's no-op that hands back its attach values; nothing stops it.
    assert posted == ["directors/dir-a/browsers/work-chrome/start"]


def test_drivenBrowser_AProfileThatWasStopped_IsStartedForTheRun_AndStoppedAgain(monkeypatch):
    posted = _fake_gateway(monkeypatch, "Stopped")

    browser = browser_ops.DrivenBrowser("work-chrome")
    browser.stop()

    assert posted == ["directors/dir-a/browsers/work-chrome/start", "directors/dir-a/browsers/work-chrome/stop"]


def test_attachedBrowser_TheDirectorStartedTheRun_TheToolNeverStopsIt(monkeypatch):
    # Run by the Director, the Director decides whether the browser is stopped after (only when it started it).
    env = dict(PROFILE_ENV, CC_LICENSE_ATTACHED_BROWSER_ID="work-chrome", CC_LICENSE_ATTACHED_BROWSER_NAME="Work Chrome")
    monkeypatch.setattr(browser_ops.gateway, "post_json", lambda *a, **k: (_ for _ in ()).throw(AssertionError("Gateway reached")))

    browser = license_signin.director_browser("Work Chrome", env)
    browser.stop()

    assert browser.environment() == PROFILE_ENV
