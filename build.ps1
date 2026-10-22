<#
.SYNOPSIS  Builds, tests and packages VMP Launcher with Velopack (framework-dependent, win-x64).
.PARAMETER Version   SemVer, e.g. 0.1.0 or 0.2.0-beta.1
.PARAMETER Channel   Velopack channel: win (stable) or beta
.PARAMETER SkipTests Skip the test run
.PARAMETER NoDelta   Do not download previous releases from GitHub for delta generation (offline / first release)
.EXAMPLE   .\build.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')][string]$Version,
    [ValidateSet('win', 'beta')][string]$Channel = 'win',
    [switch]$SkipTests,
    [switch]$NoDelta
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
. (Join-Path $repo 'eng\dotnet.ps1')
Push-Location $repo
try {
    $artifacts = Join-Path $repo 'artifacts'
    $publishDir = Join-Path $artifacts 'publish'
    $releasesDir = Join-Path $artifacts 'releases'
    New-Item -ItemType Directory -Force $artifacts, $releasesDir | Out-Null
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    Write-Host "== restore tools" -ForegroundColor Cyan
    dotnet tool restore | Out-Null

    if (-not $SkipTests) {
        Write-Host "== tests" -ForegroundColor Cyan
        dotnet test tests\VmpLauncher.Tests\VmpLauncher.Tests.csproj -c Release --nologo -v q
        if ($LASTEXITCODE) { throw 'unit tests failed' }
        dotnet test tests\VmpLauncher.IntegrationTests\VmpLauncher.IntegrationTests.csproj -c Release --nologo -v q
        if ($LASTEXITCODE) { throw 'integration tests failed' }
    }

    Write-Host "== publish $Version" -ForegroundColor Cyan
    dotnet publish src\VmpLauncher.App\VmpLauncher.App.csproj -c Release -r win-x64 --self-contained false -p:Version=$Version -p:PublishReadyToRun=false -o $publishDir --nologo
    if ($LASTEXITCODE) { throw 'publish failed' }
    # vmpl CLI ships inside the same package (host tooling), framework-dependent as well.
    dotnet publish src\VmpLauncher.Cli\VmpLauncher.Cli.csproj -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE) { throw 'cli publish failed' }
    Get-ChildItem $publishDir -Filter *.pdb | Remove-Item -Force
    $size = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("   publish folder: {0:N1} MB" -f $size)

    # Release notes from CHANGELOG.md (section for this version, if any)
    $notes = Join-Path $artifacts "notes-$Version.md"
    $changelog = Join-Path $repo 'CHANGELOG.md'
    if (Test-Path $changelog) {
        $text = Get-Content $changelog -Raw
        $m = [regex]::Match($text, "(?ms)^## \[?$([regex]::Escape($Version))\]?.*?(?=^## |\z)")
        if ($m.Success) { $m.Value | Set-Content -Encoding utf8 $notes } else { "VMP Launcher $Version" | Set-Content -Encoding utf8 $notes }
    } else { "VMP Launcher $Version" | Set-Content -Encoding utf8 $notes }

    if (-not $NoDelta) {
        Write-Host "== download previous releases for deltas" -ForegroundColor Cyan
        dotnet vpk download github --repoUrl https://github.com/Rjwolfe44/VMPLauncher --channel $Channel -o $releasesDir 2>&1 | Out-Host
    }

    Write-Host "== vpk pack" -ForegroundColor Cyan
    dotnet vpk pack -u VmpLauncher -v $Version -p $publishDir -e VmpLauncher.exe `
        --packTitle "VMP Launcher" --packAuthors Rjwolfe44 `
        -i src\VmpLauncher.App\Assets\VmpLauncher.ico --splashImage src\VmpLauncher.App\Assets\splash.png `
        -f net10.0-x64-desktop --channel $Channel --releaseNotes $notes -o $releasesDir
    if ($LASTEXITCODE) { throw 'vpk pack failed' }

    Write-Host "== artifacts" -ForegroundColor Cyan
    Get-ChildItem $releasesDir -File | ForEach-Object { "   {0,-48} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB) } | Write-Host
    Write-Host "Done. Publish with: .\publish-launcher.ps1 -Version $Version -Channel $Channel" -ForegroundColor Green
}
finally { Pop-Location }
