"""QA check: breaks the contract by exiting non-zero, with a reason on standard error."""
import sys

print("the mailbox answered 503 Service Unavailable", file=sys.stderr)
sys.exit(3)
