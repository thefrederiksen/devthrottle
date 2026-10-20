#!/usr/bin/env bash
# A throwaway PostgreSQL 16 for the local hosted Gateway, on 127.0.0.1:55432 only. Removed by clean-up.sh.
set -euo pipefail
docker run -d --name boxes-testdir-postgres -p 127.0.0.1:55432:5432 \
  -e POSTGRES_USER=testdir -e POSTGRES_PASSWORD=testdir -e POSTGRES_DB=gateway postgres:16
for i in $(seq 1 60); do docker exec boxes-testdir-postgres pg_isready -U testdir -d gateway >/dev/null 2>&1 && { echo "postgres ready"; exit 0; }; sleep 1; done
echo "postgres did not become ready" >&2; exit 1
