#!/usr/bin/env bash
# The local HOSTED test Gateway for the relay proof: the hosted launcher (the production entry point), hosted mode, a throwaway
# PostgreSQL in a container of its own, a throwaway sign-in key, every website call pointed at a closed local port, port 7996. $1 = the Boxes account list (may be empty).
H=${RIG_DIR:?set RIG_DIR to the scratch folder holding rig/ (with mint.py), root/ and home/}
GW=${TREE:?set TREE to the merged tree}/src/CcDirector.Gateway.Host/bin/Debug/net10.0
cd $GW
exec env -u CC_GATEWAY_URL -u CC_GATEWAY_SESSION_KEY -u CC_SESSION_ID -u CC_GATEWAY_STATS_DB_CONNECTION -u CC_VAULT_PATH \
  CC_DIRECTOR_ROOT=$H/root HOME=$H/home DOTNET_ROOT=/home/soren/.dotnet DOTNET_USE_POLLING_FILE_WATCHER=1 \
  CC_GATEWAY_HOSTED=1 CC_GATEWAY_DB_CONNECTION="Host=127.0.0.1;Port=55432;Username=relayproof;Password=relayproof;Database=gateway" CC_GATEWAY_NO_TAILSCALE=1 \
  CC_GATEWAY_PUBLIC_URL=https://gateway.relay-proof.invalid \
  CC_BOXES_DESKTOP_ORIGIN=http://localhost:7996 \
  DEVTHROTTLE_BOXES_TENANT_IDS="$1" \
  DEVTHROTTLE_JWT_PUBLIC_KEY_SET="$(python3 $H/rig/mint.py jwks)" \
  CC_GATEWAY_SUPABASE_ISSUER=http://127.0.0.1:7997/auth/v1 CC_GATEWAY_SUPABASE_AUDIENCE=authenticated \
  DEVTHROTTLE_API_URL=http://127.0.0.1:9/closed DEVTHROTTLE_REFRESH_URL=http://127.0.0.1:9/closed \
  ./CcDirector.Gateway.Host --port 7996
