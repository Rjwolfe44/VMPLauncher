<#
.SYNOPSIS  Uploads a packaged release to GitHub Releases (EXTERNAL ACTION). Creates a DRAFT unless -Publish.
.PARAMETER Version  Version built by build.ps1
.PARAMETER Channel  win | beta
.PARAMETER Publish  Publish the release immediately instead of leaving it as a draft
.PARAMETER Pre      Mark as pre-release
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('win', 'beta')][string]$Channel = 'win',
    [switch]$Publish,
    [switch]$Pre
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo 'eng\dotnet.ps1')
$releasesDir = Join-Path $repo 'artifacts\releases'
if (-not (Get-ChildItem $releasesDir -Filter "VmpLauncher-$Version*.nupkg" -ErrorAction SilentlyContinue)) {
    throw "No package for $Version in $releasesDir. Run build.ps1 first."
}
$token = gh auth token
if (-not $token) { throw 'gh is not logged in (gh auth login).' }
$args = @('vpk', 'upload', 'github', '--repoUrl', 'https://github.com/Rjwolfe44/VMPLauncher', '-o', $releasesDir,
          '--token', $token, '--tag', "v$Version", '--releaseName', "VMP Launcher $Version", '--channel', $Channel)
if ($Publish) { $args += '--publish' }
if ($Pre) { $args += '--pre' }
Write-Host "About to upload $Version to GitHub Releases ($(if ($Publish) { 'PUBLISHED' } else { 'draft' }))." -ForegroundColor Yellow
dotnet @args
if ($LASTEXITCODE) { throw 'vpk upload failed' }
Write-Host "Uploaded. Stable download link: https://github.com/Rjwolfe44/VMPLauncher/releases/latest/download/VmpLauncher-$Channel-Setup.exe" -ForegroundColor Green
