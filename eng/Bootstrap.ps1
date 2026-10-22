<#
.SYNOPSIS  One-time developer setup: submodule, CKAN version stub, dotnet tools, restore.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet.ps1')
Push-Location $repo
try {
    git submodule update --init --depth 1 external/CKAN
    $pinned = '1.36.4'
    $meta = Join-Path $repo 'external\CKAN\_build\meta'
    New-Item -ItemType Directory -Force $meta | Out-Null
    $major, $minor, $null = $pinned.Split('.')
@"
using System.Reflection;
[assembly: AssemblyVersion("$major.$minor")]
[assembly: AssemblyFileVersion("$pinned")]
[assembly: AssemblyInformationalVersion("$pinned")]
"@ | Set-Content -Encoding ascii (Join-Path $meta 'GlobalAssemblyVersionInfo.cs')
    dotnet tool restore
    dotnet restore VmpLauncher.slnx
    Write-Host "Bootstrap complete." -ForegroundColor Green
} finally { Pop-Location }
