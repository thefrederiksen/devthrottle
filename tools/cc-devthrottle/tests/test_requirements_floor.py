"""requirements.txt declares the same dependency floors as pyproject.toml (issue #2922 step 6a).

pyproject.toml's floors are the tested ones - the continuous integration `floor` leg installs exactly
those versions. requirements.txt still allowed Typer 0.9.0 and any Click, a range no test has ever
run, so an install from it could get a Typer that prints usage errors differently or a Click whose
test runner merges standard error into standard output.
"""

import re
from pathlib import Path

try:
    import tomllib
except ModuleNotFoundError:  # Python before 3.11 has no tomllib; the tool requires 3.11.
    tomllib = None

import pytest

TOOL = Path(__file__).parent.parent
_REQUIREMENT = re.compile(r"^\s*([A-Za-z0-9_.-]+)\s*(.*?)\s*$")


def _parse(lines):
    specs = {}
    for line in lines:
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        name, spec = _REQUIREMENT.match(line).groups()
        specs[name.lower()] = spec.replace(" ", "")
    return specs


@pytest.mark.skipif(tomllib is None, reason="tomllib needs Python 3.11, which the tool requires")
def test_requirements_txt_MatchesThePyprojectFloors():
    pyproject = tomllib.loads((TOOL / "pyproject.toml").read_text(encoding="utf-8"))
    declared = _parse(pyproject["project"]["dependencies"])
    listed = _parse((TOOL / "requirements.txt").read_text(encoding="utf-8").splitlines())

    assert listed == declared
    assert declared["typer"] == ">=0.16.1"
    assert declared["click"] == ">=8.2.1"
