<#
.SYNOPSIS  Dev helper: launches the Debug build with an isolated data dir, screenshots the main window, closes it.
.PARAMETER Out       PNG path for the screenshot.
.PARAMETER WaitSec   Seconds to wait for the window.
.PARAMETER Keep      Leave the app running.
.PARAMETER Extra     Extra launcher arguments (e.g. '--page play').
.PARAMETER DataDir   Launcher data dir (default %TEMP%mpl-devdata; use a new folder to see first-run setup).
#>
param(
    [string]$Out = (Join-Path $env:TEMP 'vmpl-shot.png'),
    [int]$WaitSec = 7,
    [switch]$Keep,
    [string]$Extra = "",
    [string]$DataDir = (Join-Path $env:TEMP 'vmpl-devdata')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path (Join-Path $repo 'src\VmpLauncher.App\bin\Debug') -Recurse -Filter VmpLauncher.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Build the App project first.' }
$data = $DataDir
$argList = @("--data-dir", "`"$data`"") + @($Extra -split " " | Where-Object { $_ })
$p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
Start-Sleep -Seconds $WaitSec
$p.Refresh()
if ($p.HasExited) {
    Write-Host "App exited with code $($p.ExitCode)." -ForegroundColor Red
    Get-ChildItem (Join-Path $data 'logs') -Filter 'crash-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { Get-Content $_.FullName }
    Get-Content (Join-Path $data 'logs\launcher-*.log') -ErrorAction SilentlyContinue | Select-Object -Last 15
    exit 1
}
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
}
"@
[W]::SetProcessDPIAware() | Out-Null
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { Start-Sleep 3; $p.Refresh(); $h = $p.MainWindowHandle }
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 900
$r = New-Object W+R
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Rt - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "Screenshot: $Out (${w}x${hh})"
if (-not $Keep) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
