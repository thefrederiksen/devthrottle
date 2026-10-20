#!/usr/bin/env bash
# The local HOSTED test Gateway: the hosted launcher (the production entry point), hosted mode, the throwaway PostgreSQL,
# a throwaway sign-in key (mint.py), every website call pointed at a closed local port, port 7996.
# $1 = the Boxes account list (may be empty). Needs RIG_DIR (the scratch folder) and TREE (the worktree it was built in).
H=${RIG_DIR:?set RIG_DIR to the scratch folder holding rig/, gateway-root/ and gateway-home/}
GW=${TREE:?set TREE to the worktree the Gateway was built in}/src/CcDirector.Gateway.Host/bin/Debug/net10.0
mkdir -p $H/gateway-root $H/gateway-home
cd $GW
exec env -u CC_GATEWAY_URL -u CC_GATEWAY_SESSION_KEY -u CC_SESSION_ID -u CC_GATEWAY_STATS_DB_CONNECTION -u CC_VAULT_PATH \
  CC_DIRECTOR_ROOT=$H/gateway-root HOME=$H/gateway-home DOTNET_ROOT=$HOME/.dotnet DOTNET_USE_POLLING_FILE_WATCHER=1 \
  CC_GATEWAY_HOSTED=1 CC_GATEWAY_DB_CONNECTION="Host=127.0.0.1;Port=55432;Username=testdir;Password=testdir;Database=gateway" CC_GATEWAY_NO_TAILSCALE=1 \
  CC_GATEWAY_PUBLIC_URL=https://gateway.test-director.invalid \
  CC_BOXES_DESKTOP_ORIGIN=http://localhost:7996 \
  DEVTHROTTLE_BOXES_TENANT_IDS="$1" \
  DEVTHROTTLE_JWT_PUBLIC_KEY_SET="$(python3 $H/rig/mint.py jwks)" \
  CC_GATEWAY_SUPABASE_ISSUER=http://127.0.0.1:7997/auth/v1 CC_GATEWAY_SUPABASE_AUDIENCE=authenticated \
  DEVTHROTTLE_API_URL=http://127.0.0.1:9/closed DEVTHROTTLE_REFRESH_URL=http://127.0.0.1:9/closed \
  ./CcDirector.Gateway.Host --port 7996
