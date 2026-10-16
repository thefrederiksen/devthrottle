"""QA check: takes 200 seconds, then finds nothing. Shows the schedule sweep is not held while a check runs."""
import time

time.sleep(200)
print('{"count": 0}')
