# Minimal stub Gateway for issue #651 proof: serves GET /account/status returning a
# signed-in DevThrottle identity, exactly the shape the real Gateway endpoint (#638) returns:
#   { "signedIn": true, "email": "...", "provider": "..." }
# Loopback only. Used to drive the Director's read-only Account panel for a screenshot.
param(
    [int]$Port = 7878,
    [string]$Email = "person@example.com",
    [string]$Provider = "google",
    [switch]$SignedOut    # when present, the stub reports signedIn:false (the not-signed-in panel state)
)
$SignedIn = -not $SignedOut

$ErrorActionPreference = "Stop"
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "[stub-gateway] listening on http://127.0.0.1:$Port (signedIn=$SignedIn email=$Email provider=$Provider)"

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $path = $ctx.Request.Url.AbsolutePath
        Write-Host "[stub-gateway] $($ctx.Request.HttpMethod) $path"

        if ($path -eq "/account/status") {
            if ($SignedIn) {
                $body = "{""signedIn"":true,""email"":""$Email"",""provider"":""$Provider""}"
            } else {
                $body = "{""signedIn"":false}"
            }
        } elseif ($path -eq "/healthz") {
            $body = "{""ok"":true}"
        } else {
            $ctx.Response.StatusCode = 404
            $body = "{""error"":""not found""}"
        }

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.OutputStream.Close()
    }
} finally {
    $listener.Stop()
}
