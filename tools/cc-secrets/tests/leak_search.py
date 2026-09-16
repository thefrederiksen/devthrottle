"""Searching files for a secret, with a search that must prove it works before its silence counts.

A leak search passes on ABSENCE, which is exactly the kind of check that certifies a run that never
happened: a wrong path, an empty file, or a decoding that never matches all come back "zero hits".
So this search refuses to report a clean result unless, for every file it searched:

1. the file exists and is not empty (otherwise it is a broken instrument, not a clean run);
2. the file contains a marker proving it covers the run being checked (the caller names it);
3. a copy of the file's own head with the secret planted in it - every form, in every encoding
   searched - IS found by the same code path. A search that cannot find a planted secret cannot
   certify its absence.

It searches the same forms and encodings the scrubber removes, so the two cannot drift apart.
"""

from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, List, Sequence, Tuple

TOOL_DIR = Path(__file__).resolve().parent.parent
if str(TOOL_DIR) not in sys.path:
    sys.path.insert(0, str(TOOL_DIR))

from src.redact import output_encodings, variants_for  # noqa: E402

PLANT_SAMPLE_BYTES = 64 * 1024


class BrokenInstrumentError(AssertionError):
    """The search could not have found a leak, so its silence proves nothing."""


@dataclass
class Hit:
    path: str
    form_index: int
    encoding: str


class SecretSearch:
    def __init__(self, secrets: Sequence[Tuple[str, str]]) -> None:
        """`secrets` is a list of (secret, username) pairs; every form of each is searched for."""
        forms = set()
        for secret, username in secrets:
            forms.update(variants_for(secret, username))
        self._forms = sorted(forms, key=len, reverse=True)
        self._encodings = output_encodings()
        self._needles = []
        for index, form in enumerate(self._forms):
            for encoding in self._encodings:
                try:
                    self._needles.append((form.encode(encoding), index, encoding))
                except UnicodeEncodeError:
                    continue

    @property
    def form_count(self) -> int:
        return len(self._forms)

    def hits_in_bytes(self, data: bytes, label: str) -> List[Hit]:
        return [Hit(label, index, encoding) for needle, index, encoding in self._needles if needle in data]

    def search_file(self, path: Path, marker: str, workdir: Path) -> List[Hit]:
        """Search one file after proving the search works on it. Returns the hits (empty is clean)."""
        if not path.exists():
            raise BrokenInstrumentError(f"{path} does not exist, so it was not searched.")
        data = path.read_bytes()
        if not data:
            raise BrokenInstrumentError(f"{path} is empty, so it cannot show whether the run leaked.")
        if marker.encode("utf-8") not in data and marker.encode("utf-16-le") not in data:
            raise BrokenInstrumentError(f"{path} does not contain the run marker '{marker}', so it does not cover this run.")
        self.prove_detects(data[:PLANT_SAMPLE_BYTES], path.name, workdir)
        return self.hits_in_bytes(data, str(path))

    def search_files(self, files: Iterable[Tuple[Path, str]], workdir: Path) -> Tuple[int, List[Hit]]:
        count = 0
        hits: List[Hit] = []
        for path, marker in files:
            hits.extend(self.search_file(path, marker, workdir))
            count += 1
        if count == 0:
            raise BrokenInstrumentError("No files were searched.")
        return count, hits

    def prove_detects(self, sample: bytes, label: str, workdir: Path) -> None:
        """Plant every form, in every encoding, in a copy of `sample` on disk; require each to be found."""
        workdir.mkdir(parents=True, exist_ok=True)
        planted_file = workdir / f"planted-{label}"
        for encoding in self._encodings:
            wanted = [(needle, index) for needle, index, enc in self._needles if enc == encoding]
            planted_file.write_bytes(sample + b"".join(b"\n noise " + needle + b" noise\n" for needle, _ in wanted))
            found = {h.form_index for h in self.hits_in_bytes(planted_file.read_bytes(), label) if h.encoding == encoding}
            missing = [index for _, index in wanted if index not in found]
            if missing:
                raise BrokenInstrumentError(f"The search could not find a planted secret in {label} ({encoding}).")
        planted_file.unlink()
