<#
.SYNOPSIS  Re-vendors the Lidgren.Network sources used by VMP into external\Lidgren.
.DESCRIPTION
  Copies every <Compile> item listed in the VMPMod shared project (Lidgren\Lidgren.projitems) except
  Properties\AssemblyInfo.cs (its assembly attributes would clash with VmpLauncher.Net), preserving the
  relative layout. Stale .cs files under external\Lidgren are removed. LICENSE (Lidgren MIT notice taken
  from the source header) and SOURCE.txt (origin + date + VMPMod commit) are regenerated. Our own
  .editorconfig / Directory.Build.props in external\Lidgren are left untouched. The source tree is only read.
.PARAMETER Source       Path to VMPMod\Lidgren (default: ..\VMP\VMPMod\Lidgren next to this repo).
.PARAMETER Destination  Path to external\Lidgren (default: this repo's external\Lidgren).
#>
param(
    [string]$Source = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'VMP\VMPMod\Lidgren'),
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'external\Lidgren')
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path $Source).Path
$projitems = Join-Path $Source 'Lidgren.projitems'
if (-not (Test-Path $projitems)) { throw "Not a Lidgren shared project: $Source" }

[xml]$xml = Get-Content -Raw $projitems
$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')
$files = $xml.SelectNodes('//m:Compile', $ns) |
    ForEach-Object { $_.Include -replace '^\$\(MSBuildThisFileDirectory\)', '' } |
    Where-Object { $_ -ne 'Properties\AssemblyInfo.cs' } |
    Sort-Object -Unique

New-Item -ItemType Directory -Force $Destination | Out-Null
$wanted = @{}
foreach ($rel in $files) {
    $from = Join-Path $Source $rel
    if (-not (Test-Path $from)) { throw "Listed in projitems but missing: $rel" }
    $to = Join-Path $Destination $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath $from -Destination $to -Force
    $wanted[(Resolve-Path $to).Path.ToLowerInvariant()] = $true
}

# Remove .cs files that are no longer part of the upstream compile list.
Get-ChildItem -Path $Destination -Recurse -Filter *.cs | Where-Object { -not $wanted.ContainsKey($_.FullName.ToLowerInvariant()) } | ForEach-Object {
    Write-Host "Removing stale $($_.FullName)"
    Remove-Item -LiteralPath $_.FullName -Force
}

# LICENSE: Lidgren's MIT notice (the header comment of every source file).
$header = Get-Content -Raw (Join-Path $Source 'NetBitVector.cs')
$m = [regex]::Match($header, '/\*(?<t>.*?)\*/', 'Singleline')
if (-not $m.Success) { throw 'Could not extract the Lidgren license header.' }
$license = "Lidgren.Network (vendored from VMP / LunaMultiplayer)`n`n" + $m.Groups['t'].Value.Trim().Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $Destination 'LICENSE'), $license, (New-Object Text.UTF8Encoding($false)))

$commit = ''
try { $commit = (& git -C $Source rev-parse HEAD 2>$null) } catch { }
$dirty = ''
try { if (& git -C $Source status --porcelain -- . 2>$null) { $dirty = ' (working tree had local changes under Lidgren\)' } } catch { }
$sourceTxt = @(
    'Origin:   VMP repository, Lidgren directory'
    'Project:  Lidgren.projitems (shared project consumed by VMPMod\Lidgren.Core\Lidgren.Core.csproj, net10.0)'
    'Commit:   ' + ($(if ($commit) { $commit } else { 'unknown' })) + $dirty
    'Copied:   ' + (Get-Date -Format 'yyyy-MM-dd')
    'Files:    ' + $files.Count + ' .cs files (Properties\AssemblyInfo.cs intentionally omitted)'
    'License:  MIT (see LICENSE)'
    'Refresh:  pwsh tools\Sync-Lidgren.ps1   (never edit these files by hand; suppress warnings in .editorconfig instead)'
) -join "`n"
[IO.File]::WriteAllText((Join-Path $Destination 'SOURCE.txt'), $sourceTxt + "`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "Vendored $($files.Count) files into $Destination"
