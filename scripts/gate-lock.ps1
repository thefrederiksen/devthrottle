#Requires -Version 5.1
<#
.SYNOPSIS
    The locks the test gate takes. Dot-source this file; it defines functions and runs nothing.

.DESCRIPTION
    TWO LOCKS, ONE RULE: A HELD LOCK IS A REFUSAL, NEVER A QUEUE.

    1. THE RELEASE-GATE LOCK (release-gate.lock). Taken by scripts\test-local.ps1 BEFORE it builds,
       whenever its run includes the Gateway suite (-Parked or -Gateway), and by
       scripts\test-qualification.ps1 for the whole soak. It says "a run that includes the Gateway suite
       is in progress on this machine for this user". A second such run finds it held and stops within
       seconds, naming the holder's process, session, commit and command. It never waits.

       WHY. Between 8 and 10 October 2026, nineteen release-gate runs each waited forty-five minutes for
       the Gateway suite's own in-process lock, then gave up having executed nothing: fourteen hours of
       gate time that proved nothing, and up to seven gates overlapping at once. Nothing stopped two
       sessions from running the release gate at the same time, and the queue hid the conflict until it
       expired. A refusal in the first second is the fix: the conflict is visible at once, the loser
       builds nothing, and the holder's name is on the screen so the right person can be asked.

    2. THE GATEWAY SUITE LOCK (gateway-test-suite.lock). Owned by the test process itself - see
       src\CcDirector.Gateway.Tests\GatewayTestSuiteLock.cs, which takes it in a module initializer so
       that a plain "dotnet test" of that project serializes without the caller knowing. This file never
       takes it for the gate. It PROBES it, so a gate can refuse before building when a hand-run
       "dotnet test" of the Gateway suite is in progress, instead of building for minutes and then
       having its child queue behind that run. The qualification soak does hold it, on purpose, for the
       whole soak: its children bypass it, and nothing else may join them.

    HOW OWNERSHIP IS DECIDED: by the operating system, never by what is written in the file. The lock is
    a file opened for writing with FileShare.Read and held open for the lifetime of the process. A second
    open for writing fails with a sharing violation. Readers are admitted, which is how a refused run
    names its blocker. A holder that dies - crash, kill, closed window - releases the handle; there is no
    stale-lock cleanup here because there is nothing for it to do, and nothing here ever takes a lock
    away from a living process. The fields written into the file are DIAGNOSTICS ONLY.

    WHERE THE LOCKS LIVE. Per user, per machine, in a place the process environment cannot move: the
    per-user local application data folder on Windows, /tmp with the user name in the file name
    elsewhere. The same derivation as GatewayTestSuiteLock.cs, for the same reason - a lock whose path
    follows TEMP is a lock two shells with different TEMP values never share.
#>

function Test-GateLockOnWindows {
    return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}

function Get-GateLockPath([string] $Name) {
    if (-not (Test-GateLockOnWindows)) {
        return "/tmp/cc-director-" + [Environment]::UserName + "-" + $Name
    }
    $localAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData, [Environment+SpecialFolderOption]::DoNotVerify)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw "Cannot locate the per-user local application data directory, so the gate lock has no environment-independent home."
    }
    return Join-Path (Join-Path (Join-Path $localAppData "cc-director") "test-locks") $Name
}

function Get-ReleaseGateLockPath { return Get-GateLockPath "release-gate.lock" }

function Get-GatewaySuiteLockPath { return Get-GateLockPath "gateway-test-suite.lock" }

# PowerShell hands a .NET constructor's exception back wrapped in a MethodInvocationException. The
# sharing-violation code is on the IOException inside it, so unwrap before reading HResult.
function Get-GateLockInnerException($Exception) {
    $e = $Exception
    while ($e -isnot [System.IO.IOException] -and $null -ne $e.InnerException) { $e = $e.InnerException }
    return $e
}

# Only a sharing conflict means "a live process holds this". Everything else - permissions, a directory
# at the path, a full disk - is a setup fault and is thrown, so it is reported as what it is instead of
# as a holder that does not exist.
function Test-GateLockSharingContention($Exception, [string] $Path) {
    $e = Get-GateLockInnerException $Exception
    if ($e -isnot [System.IO.IOException]) { return $false }
    if (Test-GateLockOnWindows) {
        $code = $e.HResult -band 0xFFFF
        return ($code -eq 32 -or $code -eq 33)   # ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }
    try {
        $probe = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        $probe.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Open-GateLockForWrite([string] $Path) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
    return New-Object System.IO.FileStream($Path, [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
}

<#
.SYNOPSIS
    Takes a lock and holds it. Returns the held lock, or $null when a live process already holds it.
    Release it with Exit-GateLock from a finally block - a script run inside an interactive shell keeps
    its process, and with it an undisposed handle, after "exit".
#>
function Enter-GateLock([string] $Path, [System.Collections.IDictionary] $Fields) {
    try {
        $stream = Open-GateLockForWrite $Path
    } catch [System.IO.IOException] {
        if (Test-GateLockSharingContention $_.Exception $Path) { return $null }
        throw
    }
    # Each element in parentheses: in PowerShell the comma binds tighter than "+", so an unbracketed
    # "a" + b, "c" is one string with the array's text pasted on, not two elements.
    $self = Get-Process -Id $PID
    $lines = @(
        ("processId=$PID"),
        ("processStartUtc=" + $self.StartTime.ToUniversalTime().ToString("o")),
        ("acquiredUtc=" + (Get-Date).ToUniversalTime().ToString("o")),
        ("machine=" + [Environment]::MachineName),
        ("user=" + [Environment]::UserName)
    )
    foreach ($key in $Fields.Keys) { $lines += "$key=$($Fields[$key])" }
    $bytes = [System.Text.Encoding]::ASCII.GetBytes((($lines -join "`n") + "`n"))
    $stream.SetLength(0)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush($true)
    return [pscustomobject]@{ Path = $Path; Stream = $stream }
}

function Exit-GateLock($Lock) {
    if ($null -ne $Lock -and $null -ne $Lock.Stream) { $Lock.Stream.Dispose() }
}

<#
.SYNOPSIS
    Probes a lock without holding it: $true when a live process holds it, $false otherwise. Writes nothing.
#>
function Test-GateLockHeld([string] $Path) {
    try {
        $stream = Open-GateLockForWrite $Path
        $stream.Dispose()
        return $false
    } catch [System.IO.IOException] {
        if (Test-GateLockSharingContention $_.Exception $Path) { return $true }
        throw
    }
}

<#
.SYNOPSIS
    Reads the diagnostics the holder wrote. A hashtable of the key=value lines, or $null if unreadable.
#>
function Read-GateLockHolder([string] $Path) {
    try {
        $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            $fields = @{}
            while ($null -ne ($line = $reader.ReadLine())) {
                $split = $line.IndexOf("=")
                if ($split -gt 0) { $fields[$line.Substring(0, $split)] = $line.Substring($split + 1) }
            }
            if ($fields.Count -eq 0) { return $null }
            return $fields
        } finally {
            $stream.Dispose()
        }
    } catch {
        return $null
    }
}

function Get-GateLockField($Holder, [string] $Key) {
    if ($null -ne $Holder -and $Holder.Contains($Key) -and -not [string]::IsNullOrWhiteSpace($Holder[$Key])) {
        return $Holder[$Key]
    }
    return "unknown"
}

<#
.SYNOPSIS
    One line a person can act on: who holds the lock, since when, from where.
#>
function Format-GateLockHolder($Holder) {
    if ($null -eq $Holder) { return "(the lock file carries no readable diagnostics)" }
    $since = Get-GateLockField $Holder "acquiredUtc"
    $held = ""
    $parsed = [DateTime]::MinValue
    if ([DateTime]::TryParse($since, [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind, [ref] $parsed)) {
        $held = " ({0:0}s ago)" -f ((Get-Date).ToUniversalTime() - $parsed.ToUniversalTime()).TotalSeconds
    }
    $parts = @(
        ("process " + (Get-GateLockField $Holder "processId") + " (started " + (Get-GateLockField $Holder "processStartUtc") + ")"),
        ("holding since $since$held"),
        ("session " + (Get-GateLockField $Holder "session")),
        ("commit " + (Get-GateLockField $Holder "commit")),
        ("command " + (Get-GateLockField $Holder "command")),
        ("directory " + (Get-GateLockField $Holder "directory"))
    )
    return ($parts -join ", ")
}

<#
.SYNOPSIS
    The session this process belongs to, for the holder line. Every fleet-launched session carries
    CC_SESSION_ID; a run started outside the fleet has no session to name and says so.
#>
function Get-GateLockSessionName {
    if ([string]::IsNullOrWhiteSpace($env:CC_SESSION_ID)) { return "(no session - identify it by the directory)" }
    return "cc-director session $env:CC_SESSION_ID"
}
