#!/usr/bin/env bash
# Ask the model both questions for every email in the raw folder, with the key supplied by cc-secrets.
# Prefers the proxy (entry devthrottle-api-key, model devthrottle/wingman-fast); otherwise DeepInfra
# directly (entry deepinfra-api-key, model Qwen/Qwen2.5-72B-Instruct). The key is never printed.
# Usage: ./run_classify.sh <raw folder outside the repository>
set -euo pipefail
raw_dir="${1:?raw folder required}"
here="$(cd "$(dirname "$0")" && pwd)"
export PATH="$HOME/.local/bin:$PATH"
names="$(cc-secrets list --json | python3 -c 'import json,sys; d=json.load(sys.stdin); d=d.get("entries",d) if isinstance(d,dict) else d; print("\n".join(e["name"] for e in d))')"
if grep -qx "devthrottle-api-key" <<<"$names"; then
  entry=devthrottle-api-key; endpoint=proxy
elif grep -qx "deepinfra-api-key" <<<"$names"; then
  entry=deepinfra-api-key; endpoint=deepinfra
else
  echo "neither cc-secrets entry devthrottle-api-key nor deepinfra-api-key exists" >&2
  exit 2
fi
echo "using cc-secrets entry $entry, endpoint $endpoint"
cc-secrets run "$entry" --via env --env-name MODEL_API_KEY --timeout 1800 -- \
  uv run --script "$here/model_sample.py" classify --raw-dir "$raw_dir" --endpoint "$endpoint" --key-env MODEL_API_KEY
