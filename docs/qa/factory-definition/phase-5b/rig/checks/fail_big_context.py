"""QA check: breaks the contract with a context of 9,000 bytes, above the 8 KB limit."""
import json

print(json.dumps({"count": 1, "context": "x" * 9000}))
