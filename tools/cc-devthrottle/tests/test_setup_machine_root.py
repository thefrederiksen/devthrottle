"""The setup commands work on the MACHINE's install, never on a Director's own folder.

``cc-devthrottle setup status`` / ``doctor`` / ``repair`` are run from inside a session more than
anywhere else, and a session inherits CC_DIRECTOR_ROOT pointing at its Director's own data folder.
While that value was taken at face value, those commands reported the install status of a folder that
holds no install, and a repair started from one of them installed a whole second copy of the tools
into it. One computer finished with seven copies, each ageing at its own pace, with whichever one
reached the search path first answering for the whole machine.

The other half is tested too: a throwaway root a test rig pins comes back untouched and keeps its own
tools. A fix that climbed out of every root would send an isolated proof's install onto the real
machine, which is worse than the fault being fixed.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import setup_ops  # noqa: E402


def test_a_directors_own_folder_resolves_to_the_machine_root_above_it():
    machine = Path("C:/Users/someone/AppData/Local/cc-director")
    assert setup_ops._machine_root(machine / "instances" / "default") == machine
    assert setup_ops._machine_root(machine / "instances" / "slot-5") == machine


def test_a_nested_directors_folder_is_climbed_all_the_way_out():
    # The leak made these: a repair run from inside a session left an
    # instances/default/instances/default on the computer that prompted this work, and one climb out
    # of that lands on another Director's folder, which is still not the machine.
    machine = Path("C:/root/cc-director")
    nested = machine / "instances" / "default" / "instances" / "default"
    assert setup_ops._machine_root(nested) == machine


def test_the_folder_name_instances_is_matched_whatever_its_case():
    machine = Path("C:/root/cc-director")
    assert setup_ops._machine_root(machine / "Instances" / "default") == machine


def test_a_rig_root_of_its_own_comes_back_exactly_as_it_went_in():
    rig = Path("C:/Temp/rig-9f2c")
    assert setup_ops._machine_root(rig) == rig

    deeper = Path("D:/work/throwaway/cc-director")
    assert setup_ops._machine_root(deeper) == deeper


def test_a_folder_called_instances_is_not_itself_a_directors_folder():
    instances = Path("C:/root/cc-director/instances")
    assert setup_ops._machine_root(instances) == instances


@pytest.fixture
def pinned_root(monkeypatch):
    """Point CC_DIRECTOR_ROOT somewhere for one test, exactly as a Director points it at a session."""

    def pin(value: Path) -> None:
        monkeypatch.setenv("CC_DIRECTOR_ROOT", str(value))

    return pin


def test_the_installer_view_from_inside_a_session_is_the_machines_install(tmp_path, pinned_root):
    machine = tmp_path / "cc-director"
    pinned_root(machine / "instances" / "slot-5")

    installer = setup_ops.DevThrottleInstaller()

    assert installer.install_root == machine
    assert installer.install_dir == machine / "bin"
    assert installer.pyenv_dir == machine / "pyenv"
    assert installer.setup_state_dir == machine / "config" / "setup"


def test_the_installer_view_in_a_rig_root_stays_inside_that_rig(tmp_path, pinned_root):
    rig = tmp_path / "rig-9f2c"
    pinned_root(rig)

    installer = setup_ops.DevThrottleInstaller()

    assert installer.install_root == rig
    assert installer.install_dir == rig / "bin"
    assert installer.pyenv_dir == rig / "pyenv"


def test_doctor_reports_the_machines_install_root(tmp_path, pinned_root):
    # The reported root is what a person reads when a tool answers from the wrong place, so it has to
    # name the folder the product is actually installed in.
    machine = tmp_path / "cc-director"
    pinned_root(machine / "instances" / "default")

    assert setup_ops.doctor_data()["installRoot"] == str(machine)


def test_no_root_setting_at_all_is_left_alone(monkeypatch):
    # An ordinary terminal has no CC_DIRECTOR_ROOT, and the platform default is already the machine
    # root. Nothing in this change may move it.
    monkeypatch.delenv("CC_DIRECTOR_ROOT", raising=False)
    root = setup_ops._install_root()

    assert root.name == "cc-director"
    assert root.parent.name.lower() != "instances"
    if os.name == "nt":
        assert root == Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director"
