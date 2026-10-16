"""QA check: keeps a state file of about 20 KB, above the 8 KB that once capped a check's output.

The first run writes it. Every later run reads it back, says whether it arrived whole (its length and its hash), and
writes it again with the run counted - so each run's state differs from the last and is stored, and what each run
found is in the state the next one reads. Prints count 0: nothing to do.
"""
import hashlib
import json
import os

PAD = "".join(f"line {i:05d} of the QA state that must arrive whole\n" for i in range(400))

path = os.environ["CC_CHECK_STATE_FILE"]
with open(path, encoding="utf-8") as fh:
    text = fh.read()
if text.strip():
    before = json.loads(text)
    whole = hashlib.sha256(before["pad"].encode("utf-8")).hexdigest() == before["padSha256"]
    state = {"run": before["run"] + 1, "previousRunFound": {"bytesRead": len(text.encode("utf-8")), "whole": whole}}
else:
    state = {"run": 1, "previousRunFound": None}
state["pad"] = PAD
state["padSha256"] = hashlib.sha256(PAD.encode("utf-8")).hexdigest()
with open(path, "w", encoding="utf-8", newline="\n") as fh:
    fh.write(json.dumps(state))
print('{"count": 0}')
