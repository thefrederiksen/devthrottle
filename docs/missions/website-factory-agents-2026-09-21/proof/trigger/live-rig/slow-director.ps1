# Make the rig Director slow to answer ONE session create, to reproduce a start whose outcome the Gateway cannot know.
#
# Watches the rig Director's log for the next "Command received: verb=create", then suspends that ONE process (the
# rig's own slot-21 Director, checked by path) for -Seconds, then resumes it. The Gateway's 30-second wait on the
# create runs out meanwhile, while the Director - resumed - still carries the create out. Suspend, never kill: every
# thread resumes where it stopped. Refuses any process that is not the rig's slot-21 Director.
#
# Usage: slow-director.ps1 -DirectorPid <pid> -Log <director log> [-Seconds 40] [-WaitMinutes 5]
param(
    [Parameter(Mandatory = $true)][int]$DirectorPid,
    [Parameter(Mandatory = $true)][string]$Log,
    [int]$Seconds = 40,
    [int]$WaitMinutes = 5
)
$ErrorActionPreference = 'Stop'

$proc = Get-Process -Id $DirectorPid
if ($proc.Path -notlike '*\devthrottle-wbf-live\scripts\local-build\cc-director21.exe') {
    throw "Refused: process $DirectorPid is $($proc.Path), not the rig's slot-21 Director."
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RigSuspend {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
}
"@

$start = (Get-Item $Log).Length
$deadline = (Get-Date).AddMinutes($WaitMinutes)
Write-Host "$(Get-Date -Format HH:mm:ss.fff) watching $Log for the next create"
while ((Get-Date) -lt $deadline) {
    $fs = [System.IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite')
    try {
        $fs.Seek($start, 'Begin') | Out-Null
        $reader = New-Object System.IO.StreamReader($fs)
        $text = $reader.ReadToEnd()
    } finally { $fs.Dispose() }
    if ($text -match 'Command received: verb=create') {
        $h = $proc.Handle
        [void][RigSuspend]::NtSuspendProcess($h)
        Write-Host "$(Get-Date -Format HH:mm:ss.fff) create received; Director $DirectorPid SUSPENDED for $Seconds s"
        try {
            Start-Sleep -Seconds $Seconds
        } finally {
            [void][RigSuspend]::NtResumeProcess($h)
            Write-Host "$(Get-Date -Format HH:mm:ss.fff) Director $DirectorPid RESUMED"
        }
        exit 0
    }
    Start-Sleep -Milliseconds 100
}
Write-Host "no create within $WaitMinutes minutes; nothing was suspended"
exit 1
