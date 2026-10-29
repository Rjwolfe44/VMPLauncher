# Resolves the pinned .NET 10 SDK and exports DOTNET_ROOT/PATH for the current PowerShell session.
# Usage:  . .\eng\dotnet.ps1 ; dotnet --version
if (-not $env:DOTNET_ROOT -or -not (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    $env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'VMPBuild\dotnet'
}
if (-not (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    throw "No .NET SDK at $env:DOTNET_ROOT. Install .NET SDK 10.0.402 there or set DOTNET_ROOT."
}
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
