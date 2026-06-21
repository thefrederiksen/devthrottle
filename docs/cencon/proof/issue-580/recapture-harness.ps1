# Re-capture proof screenshots for issue #580 using PrintWindow, which renders a specific window
# directly into a bitmap even when another window (the user's CC Director) is on top. Cropping to a
# screen rectangle failed because Windows blocks SetForegroundWindow from a background process, so a
# full-screen grab captured the occluding window. PrintWindow captures the target window itself.
# Only the slot-13 exe is stopped. ASCII output only.

param(
    [string]$Exe = "D:\ReposFred\devthrottle-wt-580\local_builds\cc-director13.exe",
    [string]$OutDir = "D:\ReposFred\devthrottle-wt-580\docs\cencon\proof\issue-580"
)

$ErrorActionPreference = "Stop"
$SigningSecret = "test-signing-secret-for-issue-583-credential-store"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static Bitmap Capture(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        Bitmap bmp = new Bitmap(w, ht);
        using (Graphics g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            // flag 2 = PW_RENDERFULLCONTENT (captures DirectX/Avalonia composited content)
            PrintWindow(h, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }
}
"@ -ReferencedAssemblies System.Drawing

function New-TestJwt([datetime]$expiresUtc, [string]$secret) {
    function B64Url([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_') }
    $exp = [DateTimeOffset]::new($expiresUtc, [TimeSpan]::Zero).ToUnixTimeSeconds()
    $header = B64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}'))
    $payload = B64Url ([Text.Encoding]::UTF8.GetBytes('{"sub":"test-user","exp":' + $exp + '}'))
    $signingInput = "$header.$payload"
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $sig = B64Url ($hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes($signingInput)))
    return "$signingInput.$sig"
}

function Stop-Slot { Get-Process -Name "cc-director13" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | ForEach-Object { Stop-Process -Id $_.Id -Force } }

function Find-SlotWindow {
    for ($i = 0; $i -lt 20; $i++) {
        $w = Get-Process -Name "cc-director13" -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Run([string]$Name, [string]$Root, [hashtable]$EnvVars, [string]$Shot) {
    Write-Host "=== $Name ==="
    if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Stop-Slot
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["CC_DIRECTOR_ROOT"] = $Root
    foreach ($k in $EnvVars.Keys) { $psi.EnvironmentVariables[$k] = $EnvVars[$k] }
    $proc = [System.Diagnostics.Process]::Start($psi)
    Write-Host "[recap] PID $($proc.Id)"
    Start-Sleep -Seconds 11
    $win = Find-SlotWindow
    if (-not $win) { Stop-Slot; throw "No slot-13 window found for $Name" }
    Start-Sleep -Milliseconds 1500  # let the window fully render
    $bmp = [Win]::Capture($win.MainWindowHandle)
    $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "[recap] shot -> $Shot ($($win.MainWindowTitle))"
    Stop-Slot
}

Run "no-credential" "$env:TEMP\cc-dt-580-A" @{} (Join-Path $OutDir "scenario-a-no-credential.png")

$jwtB = New-TestJwt ((Get-Date).ToUniversalTime().AddHours(1)) $SigningSecret
Run "credential-online" "$env:TEMP\cc-dt-580-B" @{
    "DEVTHROTTLE_JWT_SIGNING_SECRET" = $SigningSecret
    "DEVTHROTTLE_TEST_SEED_TOKEN" = "$jwtB`ntest-refresh-token"
} (Join-Path $OutDir "scenario-b-credential-online.png")

$jwtC = New-TestJwt ((Get-Date).ToUniversalTime().AddHours(-1)) $SigningSecret
Run "credential-offline" "$env:TEMP\cc-dt-580-C" @{
    "DEVTHROTTLE_JWT_SIGNING_SECRET" = $SigningSecret
    "DEVTHROTTLE_TEST_SEED_TOKEN" = "$jwtC`ntest-refresh-token"
} (Join-Path $OutDir "scenario-c-credential-offline.png")

Write-Host "[recap] done"
