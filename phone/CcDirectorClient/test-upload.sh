#!/usr/bin/env bash
# Self-test harness for the phone recorder upload + progress UI.
#
# Builds a synthetic multi-segment recording on the phone from one real audio
# segment, wipes any server-side copy, resets the phone manifest to Queued, then
# force-stops + cold-launches the app so it re-uploads from scratch. Lets you
# watch the determinate progress bar move through the sending and transcribing
# phases. Recording audio is never deleted from the phone.
#
# Usage: test-upload.sh [segments]   (default 6)
set -u
export ANDROID_HOME="${ANDROID_HOME:-/c/Users/$USERNAME/AppData/Local/Android/Sdk}"
export MSYS_NO_PATHCONV=1
ADB="$ANDROID_HOME/platform-tools/adb.exe"
# adb-over-wifi target, e.g. ADB_DEVICE=100.x.y.z:port (your phone's tailscale ip)
D="${ADB_DEVICE:?Set ADB_DEVICE=<ip>:<port> (adb over wifi)}"
PKG=com.ccdirector.client
REC_ROOT=/sdcard/Android/data/$PKG/files/recordings
SRC_M4A="C:/Users/$USERNAME/AppData/Local/Temp/ccfix/src.m4a"
TMP="C:/Users/$USERNAME/AppData/Local/Temp/ccfix"

N=${1:-6}
ID="f1f1f1f1f1f1f1f1f1f1f1f1f1f1f101"   # fixed id so re-runs reuse the same recording
SHA="60f8c0ab718abd2153bd6d40583b4ecb88c415dcc258464a47fb48a11933d5e8"
BYTES=94702
DUR=13575
# your CC Director Gateway front door (Tailscale Serve hostname)
SERVER="${CC_GATEWAY_URL:?Set CC_GATEWAY_URL=https://<your-gateway>.ts.net}"

echo "=== [1/5] keep screen awake ==="
"$ADB" -s $D shell svc power stayon true >/dev/null 2>&1
"$ADB" -s $D shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1
"$ADB" -s $D shell wm dismiss-keyguard >/dev/null 2>&1

echo "=== [2/5] build $N-segment fixture locally ==="
FIX="$TMP/$ID"
rm -rf "$FIX"; mkdir -p "$FIX"
chunks=""
for ((i=0;i<N;i++)); do
  printf -v idx "%04d" "$i"
  cp "$SRC_M4A" "$FIX/$idx.m4a"
  start=$((i*DUR))
  sep=""; [ $i -gt 0 ] && sep=","
  chunks="$chunks$sep
    { \"Index\": $i, \"File\": \"$idx.m4a\", \"StartMs\": $start, \"DurationMs\": $DUR, \"Bytes\": $BYTES, \"Sha256\": \"$SHA\", \"Uploaded\": false }"
done
cat > "$FIX/manifest.json" <<JSON
{
  "RecordingId": "$ID",
  "Title": "Progress Test ($N segments)",
  "DeviceId": "SM-F721W",
  "StartedAt": "2026-05-24T12:00:00.0000000Z",
  "EndedAt": "2026-05-24T12:00:$(printf '%02d' $((N*14%60))).0000000Z",
  "SampleRateHz": 16000,
  "Channels": 1,
  "Codec": "aac-m4a",
  "Chunks": [$chunks
  ],
  "Notes": [],
  "State": "Queued",
  "VaultDocId": null,
  "Transcript": null,
  "UploadError": null,
  "UploadProgress": null,
  "UploadPhase": null,
  "UploadCurrent": 0,
  "UploadTotal": 0
}
JSON

echo "=== [3/5] wipe server copy (so it re-transcribes) ==="
curl -sk -X DELETE "$SERVER/ingest/recording/$ID" -o /dev/null -w "  DELETE -> %{http_code}\n" 2>&1 || echo "  (delete failed/none, continuing)"

echo "=== [4/5] push fixture to phone ==="
"$ADB" -s $D shell rm -rf "$REC_ROOT/$ID" >/dev/null 2>&1
"$ADB" -s $D push "$FIX" "$REC_ROOT/" 2>&1 | tail -1

echo "=== [5/5] cold-launch app ==="
"$ADB" -s $D shell am force-stop $PKG >/dev/null 2>&1
"$ADB" -s $D shell monkey -p $PKG -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
echo "launched. recording id=$ID, segments=$N"
