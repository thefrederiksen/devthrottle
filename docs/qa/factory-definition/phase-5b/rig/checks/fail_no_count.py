"""QA check: breaks the contract by printing a JSON object with no count."""
import json

print(json.dumps({"found": 2}))
