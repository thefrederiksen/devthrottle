"""QA check: breaks the contract by running past its timeout (five minutes for a start every 24h)."""
import time

time.sleep(330)
print('{"count": 0}')
