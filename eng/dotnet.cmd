@echo off
rem Runs the pinned .NET 10 SDK (not the SDK 9 on PATH).
if "%DOTNET_ROOT%"=="" set "DOTNET_ROOT=%LOCALAPPDATA%\VMPBuild\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
"%DOTNET_ROOT%\dotnet.exe" %*
