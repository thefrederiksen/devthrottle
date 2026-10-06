@echo off
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
REM NOT ISOLATED. No variable and no root setting keeps a Director from the rest of the computer (finding F8).
REM Even with every CC_* variable cleared, a run on the owner's computer still:
REM   watches every folder of every fixed drive and deletes files named nul
REM   looks through %USERPROFILE% and the drive roots for code folders, enrolls them and runs git in their repositories
REM   runs the owner's installed agent tools (a version check on each) and the installed agent for every session
REM   writes %USERPROFILE%\.agents\skills and %USERPROFILE%\.claude\skills, adding and REMOVING skills (F7)
REM   writes the agent's per-user transcripts under %USERPROFILE%\.claude\projects
REM   scans %USERPROFILE%\.claude\backups with a cleaner that can delete
REM   runs the owner's installed command line tools found through the user PATH
REM   opens the owner's default browser for each sign-in, and writes scripts\local-build\standby beside the slot
REM Run the next one on a clean machine or as a separate Windows user.
set "DEVTHROTTLE_JWT_PUBLIC_KEY_SET={"keys": [{"kty": "EC", "crv": "P-256", "alg": "ES256", "use": "sig", "kid": "teams-2311-liveproof", "x": "5ogG37FJwJIJpTT-liljmWg6qAJ3VgL2hM5USanCRl0", "y": "cdtQ3orO5-hTDR6fLs1e-MFe5Tw91TPgoLcnyWpWbrc"}]}"
set "DEVTHROTTLE_API_URL=http://127.0.0.1:7952/website-stub"
set "DEVTHROTTLE_REFRESH_URL=http://127.0.0.1:7952/refresh-stub"
set "CC_DIRECTOR_ROOT=D:\ReposFred\devthrottle-teams-2311-liveproof\.liveproof-rig\director-root"
set "CC_AUTOUPDATE=0"
set "DEVTHROTTLE_HOSTED_GATEWAY_URL=http://127.0.0.1:7951"
set "DEVTHROTTLE_SIGNIN_URL=http://127.0.0.1:7952/signin"
cd /d "D:\ReposFred\devthrottle-teams-2311-liveproof\scripts\local-build"
"D:\ReposFred\devthrottle-teams-2311-liveproof\scripts\local-build\cc-director5.exe" --instance teams-proof-a
