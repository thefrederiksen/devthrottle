@echo off
title teams-2311 liveproof - LOCAL hosted Gateway 7951
set "CC_GATEWAY_DB_CONNECTION="
set "CC_GATEWAY_STATS_DB_CONNECTION="
set "CC_GATEWAY_NO_AUTH="
set "CC_GATEWAY_AUTH="
set "CC_GATEWAY_URL="
set "CC_GATEWAY_SESSION_KEY="
set "CC_SESSION_ID="
set "CC_COCKPIT_MANAGED="
REM CC_VAULT_PATH is set at user level on this computer to the owner's vault, and it wins over CC_DIRECTOR_ROOT:
REM left in place, the process opens the owner's engine database. Cleared here (review finding L-F1).
set "CC_VAULT_PATH="
set "DEVTHROTTLE_JWT_PUBLIC_KEY_SET={"keys": [{"kty": "EC", "crv": "P-256", "alg": "ES256", "use": "sig", "kid": "teams-2311-liveproof", "x": "5ogG37FJwJIJpTT-liljmWg6qAJ3VgL2hM5USanCRl0", "y": "cdtQ3orO5-hTDR6fLs1e-MFe5Tw91TPgoLcnyWpWbrc"}]}"
set "DEVTHROTTLE_API_URL=http://127.0.0.1:7952/website-stub"
set "DEVTHROTTLE_REFRESH_URL=http://127.0.0.1:7952/refresh-stub"
set "CC_DIRECTOR_ROOT=D:\ReposFred\devthrottle-teams-2311-liveproof\.liveproof-rig\gateway-root"
set "CC_GATEWAY_HOSTED=1"
set "CC_GATEWAY_TEAMS=1"
set "CC_GATEWAY_NO_TAILSCALE=1"
set "CC_GATEWAY_SUPABASE_ISSUER=http://127.0.0.1:7952/auth/v1"
set "CC_GATEWAY_SUPABASE_AUDIENCE=authenticated"
set "CC_GATEWAY_PUBLIC_URL=http://127.0.0.1:7951"
"D:\ReposFred\devthrottle-teams-2311-liveproof\src\CcDirector.Gateway\bin\Debug\net10.0\CcDirector.Gateway.exe" --port 7951
