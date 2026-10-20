#!/usr/bin/env bash
# Factory Boxes phase 1, the desktop relay leg: the refusals, captured as plain requests against the local test Gateway.
# Usage: refusals.sh <port> <token> <box id>   (writes every request and its answer to standard output)
set -u
PORT="$1"; TOKEN="$2"; BOX="$3"
MAIN="http://127.0.0.1:$PORT"; DESK="http://localhost:$PORT"

show() { # label, then curl arguments; prints the request line, the status and the body
  local label="$1"; shift
  echo "=== $label"
  echo "\$ curl $(printf '%q ' "$@" | sed -E "s/$TOKEN/<token>/g; s/(cc-box-desktop=)[A-Za-z0-9_-]+/\\1<session>/g; s/ticket=[A-Za-z0-9_%-]+/ticket=<redacted>/g")"
  curl -s -o /tmp/relay-refusal-body -D /tmp/relay-refusal-head -w "-> HTTP %{http_code}\n" "$@"
  grep -iE "^(location|set-cookie|content-type):" /tmp/relay-refusal-head | sed -E 's/(cc-box-desktop=)[^;]+/\1<session>/; s/ticket=[^&"]+/ticket=<redacted>/'
  head -c 300 /tmp/relay-refusal-body | sed -E 's/ticket=[^&"]+/ticket=<redacted>/'; echo; echo
}

show "1. Signed out: the Cockpit's ticket request with no sign-in" -X POST "$MAIN/boxes/$BOX/desktop-ticket"
show "2. Signed out: the desktop page on the desktop host with no box cookie" "$DESK/boxes/$BOX/desktop/"
show "3. Signed out: the desktop WebSocket upgrade with no box cookie" -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" "$DESK/boxes/$BOX/desktop/api/websockets"
show "4. The Gateway's own sign-in is not a key to the desktop host" -H "Authorization: Bearer $TOKEN" "$DESK/boxes/$BOX/desktop/"
show "5. Unknown box id: no connected Director of this account hosts it" -X POST -H "Authorization: Bearer $TOKEN" "$MAIN/boxes/no-such-box/desktop-ticket"
show "6. Not a box id" -X POST -H "Authorization: Bearer $TOKEN" "$MAIN/boxes/Not_A_Box/desktop-ticket"
show "7. The desktop route does not exist on the main host" -H "Authorization: Bearer $TOKEN" "$MAIN/boxes/$BOX/desktop/"
show "8. The desktop host serves nothing else: the session list" -H "Authorization: Bearer $TOKEN" "$DESK/sessions"
show "9. The desktop host serves nothing else: the ticket route" -X POST -H "Authorization: Bearer $TOKEN" "$DESK/boxes/$BOX/desktop-ticket"
show "10. ... but /healthz still answers there, for the deploy workflows" "$DESK/healthz"

URL=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" "$MAIN/boxes/$BOX/desktop-ticket" | sed -E 's/.*"url":"([^"]+)".*/\1/')
show "11. A ticket, redeemed once: the box cookie and a page that moves on without the ticket" "$URL"
show "12. The same ticket again: refused" "$URL"
COOKIE=$(curl -s -D - -o /dev/null "$(curl -s -X POST -H "Authorization: Bearer $TOKEN" "$MAIN/boxes/$BOX/desktop-ticket" | sed -E 's/.*"url":"([^"]+)".*/\1/')" | grep -i "^set-cookie:" | sed -E 's/^[Ss]et-[Cc]ookie: ([^;]+);.*/\1/')
show "13. The box cookie opens this box's desktop" -H "Cookie: $COOKIE" "$DESK/boxes/$BOX/desktop/api/status"
show "14. The same cookie does not open another box's path" -H "Cookie: $COOKIE" "$DESK/boxes/other-box/desktop/"
show "15. No trailing slash: a redirect that stays on the desktop host, never 127.0.0.1" -H "Cookie: $COOKIE" "$DESK/boxes/$BOX/desktop?x=1"
