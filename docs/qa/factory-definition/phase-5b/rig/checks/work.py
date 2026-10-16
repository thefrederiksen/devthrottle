"""QA check: finds work. Prints count 2 and a context the seat's agent must see in its prompt and in check.json."""
import json

print(json.dumps({"count": 2, "context": "QA-P5B-TIMED-FOUND: two items found by the timed check (item-a, item-b)"}))
