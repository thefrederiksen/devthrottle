@echo off
set CC_DIRECTOR_ROOT=C:\Users\soren\AppData\Local\Temp\wbf-qa\root
set CC_GATEWAY_NO_TAILSCALE=1
set CC_GATEWAY_NO_AUTH=1
"C:\Users\soren\AppData\Local\Temp\wbf-qa\stage\devthrottle-gateway.exe" --port 7899 --no-autostart
