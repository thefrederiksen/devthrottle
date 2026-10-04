<#
.SYNOPSIS
  Money Saver, item 3: measure what one more account costs the Gateway in memory and data out.

.DESCRIPTION
  For each step (number of accounts) this starts a PRIVATE hosted Gateway in its own process on a throwaway folder,
  lets it settle with nothing connected, then drives that many simulated accounts against it for the hold time,
  and stops it. Each step is a fresh Gateway, so one step's leftovers never count toward the next.

  Results land in <RunRoot>\<profile>-<n>\: memory.csv (the Gateway process's memory every few seconds),
  collected.csv (one live-memory sample after a full collection), traffic.json (each account's own traffic meter
  rows), drive.json (what the driver did), the Gateway's log, and the two processes' console output.
  summarize.py turns a run root into the per-account figures.

  It never touches production and never the machine's real DevThrottle folder: the Gateway's data root is set to the
  step's own folder, and the host refuses to start if it is not.

.EXAMPLE
  .\run-load.ps1 -Profile fleet -Steps 0,5,10,20 -HoldMinutes 15
  .\run-load.ps1 -Profile fleet -Steps 0,5,10 -HoldMinutes 15 -Today
#>
param(
    [ValidateSet('fleet', 'light')] [string] $Profile = 'fleet',
    [int[]] $Steps = @(0, 5, 10, 20),
    [double] $HoldMinutes = 15,
    [int] $SettleSeconds = 60,
    [switch] $Today,
    [switch] $SkipBuild,
    [string] $RunRoot = (Join-Path $env:TEMP ("gateway-load-" + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'GatewayLoad.csproj'
if (-not $SkipBuild) {
    Write-Host "[run-load] building $project"
    dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\gateway-load.exe'
if (-not (Test-Path $exe)) { throw "built tool not found at $exe" }

$directors = if ($Profile -eq 'fleet') { 3 } else { 1 }
$suffix = if ($Today) { '-today' } else { '' }
New-Item -ItemType Directory -Force $RunRoot | Out-Null
Write-Host "[run-load] results in $RunRoot"

# The host must run on its own throwaway folder and must not reach a real database or address. These are set or
# cleared for the processes this script starts, and put back as they were when it ends, so nothing started later
# from the same terminal inherits a hosted, throwaway configuration. The list matches HostMode.RefusedSettings.
$managed = @('CC_DIRECTOR_ROOT', 'CC_GATEWAY_HOSTED', 'CC_GATEWAY_DB_CONNECTION', 'CC_GATEWAY_STATS_DB_CONNECTION',
             'CC_GATEWAY_PUBLIC_URL', 'CC_GATEWAY_EF_PROVIDER')
$saved = @{}
foreach ($name in $managed) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    foreach ($n in $Steps) {
        $run = Join-Path $RunRoot "$Profile$suffix-$n"
        # A step folder that already holds a run would mix old files (an old stop.txt, accounts.json or traffic) into
        # the new one. Refused, never cleared: the old results are someone's evidence.
        if ((Test-Path $run) -and (Get-ChildItem $run -Force | Select-Object -First 1)) {
            throw "step folder $run already holds a run; use a new -RunRoot or move that folder away"
        }
        New-Item -ItemType Directory -Force $run | Out-Null
        foreach ($name in $managed) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
        $env:CC_DIRECTOR_ROOT = Join-Path $run 'root'
        $env:CC_GATEWAY_HOSTED = '1'

        Write-Host "[run-load] step $n accounts: starting the host"
        $hostProcess = Start-Process -FilePath $exe -PassThru -NoNewWindow `
            -ArgumentList @('host', '--run', $run, '--accounts', $n, '--directors', $directors) `
            -RedirectStandardOutput (Join-Path $run 'host.out.txt') -RedirectStandardError (Join-Path $run 'host.err.txt')
        $null = $hostProcess.Handle   # so ExitCode is readable later (see the driver below)
        try {
            $deadline = (Get-Date).AddMinutes(3)
            while (-not (Test-Path (Join-Path $run 'accounts.json'))) {
                if ($hostProcess.HasExited) { throw "host exited with $($hostProcess.ExitCode); see $run\host.err.txt" }
                if ((Get-Date) -gt $deadline) { throw "host did not enroll accounts within 3 minutes; see $run\host.out.txt" }
                Start-Sleep -Seconds 1
            }
            Write-Host "[run-load] step $n accounts: settling $SettleSeconds s with nothing connected"
            Start-Sleep -Seconds $SettleSeconds

            if ($n -gt 0) {
                $driveArgs = @('drive', '--run', $run, '--profile', $Profile, '--count', $n,
                    '--minutes', $HoldMinutes.ToString([Globalization.CultureInfo]::InvariantCulture))
                if ($Today) { $driveArgs += '--today' }
                # The driver reports a failed read on stderr and carries on; Windows PowerShell would turn that line
                # into a terminating error, so the driver's streams go to files and only its exit code decides.
                $drive = Start-Process -FilePath $exe -ArgumentList $driveArgs -PassThru -NoNewWindow `
                    -RedirectStandardOutput (Join-Path $run 'drive.txt') -RedirectStandardError (Join-Path $run 'drive.err.txt')
                # Windows PowerShell reports no exit code for a process started this way unless its handle is taken
                # while it runs; without this line ExitCode reads empty and a clean run looks like a failure.
                $null = $drive.Handle
                $limitMs = [int](($HoldMinutes + 5) * 60000)
                if (-not $drive.WaitForExit($limitMs)) {
                    throw "driver for step $n did not finish within the hold plus 5 minutes (pid $($drive.Id)); stop it by hand"
                }
                Get-Content (Join-Path $run 'drive.txt')
                $failed = @(Get-Content (Join-Path $run 'drive.err.txt'))
                if ($failed.Count -gt 0) { Write-Host "[run-load] step ${n}: $($failed.Count) driver error line(s), first: $($failed[0])" }
                if ($drive.ExitCode -ne 0) { throw "driver exited with $($drive.ExitCode); see $run\drive.err.txt" }
            } else {
                # No accounts: take the same live-memory sample the driver asks for at the end of a hold.
                Start-Sleep -Seconds ([int]($HoldMinutes * 60))
                Set-Content -Path (Join-Path $run 'collect.txt') -Value (Get-Date -Format o)
                $deadline = (Get-Date).AddSeconds(60)
                while (Test-Path (Join-Path $run 'collect.txt')) {
                    if ((Get-Date) -gt $deadline) { throw "host did not take the live-memory sample within 60 s" }
                    Start-Sleep -Milliseconds 500
                }
            }
        }
        finally {
            Set-Content -Path (Join-Path $run 'stop.txt') -Value (Get-Date -Format o)
            if (-not $hostProcess.WaitForExit(60000)) {
                throw "host for step $n did not stop within 60 s after stop.txt (pid $($hostProcess.Id)); stop it by hand"
            }
        }
        Write-Host "[run-load] step $n accounts: done"
    }
}
finally {
    foreach ($name in $managed) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
Write-Host "[run-load] all steps done. Summarize with: python $PSScriptRoot\summarize.py $RunRoot"
