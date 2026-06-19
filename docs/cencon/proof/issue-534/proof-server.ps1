# Proof harness server for issue #534 (/m/ auto-refresh).
# Serves the EXACT committed /m/ assets (m.js, m.css, index.html, sw.js) from the worktree wwwroot,
# plus a controllable stub of the gateway endpoints the page calls (/sessions, /wingman/voice/ready,
# /wingman/menu, /wingman/voice). A /_control endpoint flips the stub session state so the proof can
# change "the gateway's" state and watch the page reflect it. A /_log endpoint returns the request log
# so the proof can prove no /sessions requests fire while the tab is hidden. ASCII only.
param([int]$Port = 8534)

$ErrorActionPreference = "Stop"
$wwwroot = Join-Path $PSScriptRoot "..\..\..\..\src\CcDirector.Cockpit\wwwroot\m"
$wwwroot = (Resolve-Path $wwwroot).Path

# Shared mutable state (single-threaded listener loop).
$script:state = "WaitingForInput"
$script:color = "red"
$script:reqLog = New-Object System.Collections.ArrayList

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "PROOF-SERVER LISTENING http://127.0.0.1:$Port/"
Write-Host "WWWROOT $wwwroot"

function Send-Json($ctx, $obj) {
  $json = $obj | ConvertTo-Json -Depth 6 -Compress
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
  $ctx.Response.ContentType = "application/json"
  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  $ctx.Response.OutputStream.Close()
}

function Send-File($ctx, $path, $type) {
  $bytes = [System.IO.File]::ReadAllBytes($path)
  $ctx.Response.ContentType = $type
  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  $ctx.Response.OutputStream.Close()
}

while ($listener.IsListening) {
  $ctx = $listener.GetContext()
  try {
    $path = $ctx.Request.Url.AbsolutePath
    $ts = (Get-Date).ToString("HH:mm:ss.fff")
    [void]$script:reqLog.Add("$ts $($ctx.Request.HttpMethod) $path")

    if ($path -eq "/_control") {
      $q = $ctx.Request.QueryString
      if ($q["state"]) { $script:state = $q["state"] }
      if ($q["color"]) { $script:color = $q["color"] }
      Send-Json $ctx @{ ok = $true; state = $script:state; color = $script:color }
    }
    elseif ($path -eq "/_log") {
      Send-Json $ctx @{ count = $script:reqLog.Count; lines = @($script:reqLog) }
    }
    elseif ($path -eq "/_logclear") {
      $script:reqLog.Clear()
      Send-Json $ctx @{ ok = $true }
    }
    elseif ($path -eq "/sessions") {
      $session = @{
        sessionId = "sess-1"; name = "Proof Session"; repoPath = "C:/repos/proof";
        machineName = "proof-box"; statusColor = $script:color;
        assessedState = $script:state; activityState = $script:state;
        lastActivityAt = (Get-Date).ToString("o"); onHold = $false; briefingState = "Idle"
      }
      Send-Json $ctx @{ sessions = @($session) }
    }
    elseif ($path -eq "/wingman/voice/ready") {
      Send-Json $ctx @{ sids = @() }
    }
    elseif ($path -like "*/wingman/menu") {
      Send-Json $ctx @{ isMenu = $false }
    }
    elseif ($path -like "*/wingman/voice") {
      Send-Json $ctx @{ ready = $false }
    }
    elseif ($path -eq "/" -or $path -eq "/m/" -or $path -eq "/m/index.html") {
      Send-File $ctx (Join-Path $wwwroot "index.html") "text/html"
    }
    elseif ($path -like "/m/*.js") {
      Send-File $ctx (Join-Path $wwwroot ([System.IO.Path]::GetFileName($path))) "application/javascript"
    }
    elseif ($path -like "/m/*.css") {
      Send-File $ctx (Join-Path $wwwroot ([System.IO.Path]::GetFileName($path))) "text/css"
    }
    else {
      $ctx.Response.StatusCode = 404
      $ctx.Response.OutputStream.Close()
    }
  } catch {
    try { $ctx.Response.StatusCode = 500; $ctx.Response.OutputStream.Close() } catch {}
  }
}
