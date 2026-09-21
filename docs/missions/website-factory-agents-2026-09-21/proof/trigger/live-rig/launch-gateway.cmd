@echo off
set CC_DIRECTOR_ROOT=C:\Users\soren\AppData\Local\Temp\wbf-live\gw-root
set CC_GATEWAY_NO_TAILSCALE=1
set CC_GATEWAY_NO_AUTH=1
"C:\Users\soren\AppData\Local\Temp\wbf-live\gw-stage2\devthrottle-gateway.exe" --port 7898 --no-autostart
