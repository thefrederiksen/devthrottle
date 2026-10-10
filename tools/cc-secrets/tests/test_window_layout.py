"""Where the cc-secrets windows open, and what the list remembers - all worked out without a screen.

The owner's report (2026-10-10) that started this: with the list on his second screen, Edit opened its form in
the corner of the main screen. The monitors below are his: a second screen to the right of the main one, and a
third below them.
"""

import json

import pytest

from src import window_layout as wl
from src.window_actions import SHOW_ALL, SHOW_SETTINGS

SECOND = (1920, -5, 3840, 1075)
MAIN = (0, 0, 1920, 1032)
THIRD = (1173, 1080, 2539, 1848)
MONITORS = [SECOND, MAIN, THIRD]


def test_AFormOpensCentredOverTheList_OnTheListsMonitor():
    the_list = (2108, 151, 3168, 771)  # on the second screen

    x, y = wl.centre_over(the_list, 600, 560, MONITORS)

    assert SECOND[0] <= x and x + 600 <= SECOND[2]
    assert (x + 300, y + 280) == ((2108 + 3168) // 2, (151 + 771) // 2)


def test_AFormOverAListAtTheMonitorsEdge_IsKeptOnThatMonitor():
    the_list = (3500, 900, 3840, 1075)  # mostly off the bottom right of the second screen

    x, y = wl.centre_over(the_list, 600, 560, MONITORS)

    assert (x + 600, y + 560) <= (SECOND[2], SECOND[3])
    assert x >= SECOND[0] and y >= SECOND[1]


def test_TheAskPopUp_OpensOnTheMonitorUnderTheMouse():
    x, y = wl.centre_on_monitor((1500, 1400), 560, 700, MONITORS)  # mouse on the third screen

    assert THIRD[0] <= x and x + 560 <= THIRD[2] and THIRD[1] <= y and y + 700 <= THIRD[3]


def test_APointBetweenMonitors_GoesToTheNearest():
    assert wl.monitor_at(1000, 1060, MONITORS) == MAIN  # in the gap below the main screen's work area


def test_AWindowBiggerThanTheMonitor_KeepsItsTitleBarOnScreen():
    assert wl.clamp_into(100, 100, 5000, 3000, MAIN) == (0, 0)


def test_TheListReopensWhereItWasClosed():
    prefs = wl.ListPrefs(x=2200, y=200, width=1060, height=620)

    assert wl.list_geometry(prefs, MONITORS, (10, 10)) == (2200, 200, 1060, 620)


def test_TheListClosedOnAMonitorSinceUnplugged_OpensUnderTheMouseInstead():
    prefs = wl.ListPrefs(x=4200, y=200, width=1060, height=620)  # a fourth screen that is gone

    x, y, width, height = wl.list_geometry(prefs, MONITORS, (500, 500))

    assert MAIN[0] <= x and x + width <= MAIN[2] and MAIN[1] <= y and y + height <= MAIN[3]


def test_TheListOpenedTheFirstTime_IsCentredUnderTheMouse_AndNeverBiggerThanTheMonitor():
    x, y, width, height = wl.list_geometry(wl.ListPrefs(width=3000, height=3000), MONITORS, (1500, 1400))

    assert (width, height) == (THIRD[2] - THIRD[0], THIRD[3] - THIRD[1])
    assert (x, y) == (THIRD[0], THIRD[1])


def test_TheListsFrame_TitleBarIncluded_EndsInsideTheMonitor():
    assert wl.fit_height(top=0, title_bar=31, height=1032, monitor=MAIN, minimum=420) == 1001
    assert wl.fit_height(top=100, title_bar=31, height=600, monitor=MAIN, minimum=420) == 600
    assert wl.fit_height(top=900, title_bar=31, height=600, monitor=MAIN, minimum=420) == 420  # never below minimum


def test_StillVisible_NeedsEnoughOfTheTitleBarToGrab():
    assert wl.still_visible((1900, 100, 2900, 700), MONITORS)
    assert not wl.still_visible((3830, 100, 4830, 700), MONITORS)  # only 10 pixels on the second screen


def test_Prefs_RoundTrip(tmp_path):
    path = tmp_path / "window.json"
    saved = wl.ListPrefs(x=-1900, y=40, width=1200, height=700, sort_column="used", sort_descending=True,
                         show=SHOW_SETTINGS)

    wl.save_prefs(path, saved)

    assert wl.load_prefs(path) == saved


def test_Prefs_HoldNothingAboutAnyEntry(tmp_path):
    path = tmp_path / "window.json"
    wl.save_prefs(path, wl.ListPrefs(x=1, y=2))

    assert set(json.loads(path.read_text(encoding="utf-8"))) == \
        {"x", "y", "width", "height", "sort_column", "sort_descending", "show"}


def test_Prefs_NoneYet_AreTheDefaults(tmp_path):
    assert wl.load_prefs(tmp_path / "missing.json") == wl.ListPrefs()


@pytest.mark.parametrize("text", ["not json", "[1, 2]", '{"x": "left"}'])
def test_Prefs_NotUnderstood_AreTheDefaults_AndLogged(tmp_path, monkeypatch, text):
    logged = []
    monkeypatch.setattr(wl.filelog, "write", logged.append)
    path = tmp_path / "window.json"
    path.write_text(text, encoding="utf-8")

    assert wl.load_prefs(path) == wl.ListPrefs()
    assert logged and "not understood" in logged[0]


def test_Prefs_AnUnknownSortOrFilter_IsReplaced(tmp_path):
    path = tmp_path / "window.json"
    path.write_text(json.dumps({"sort_column": "secret", "show": "everything", "width": 10}), encoding="utf-8")

    prefs = wl.load_prefs(path)

    assert (prefs.sort_column, prefs.show, prefs.width) == ("name", SHOW_ALL, 640)
