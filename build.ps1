param([switch]$Launch)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.local\dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $PSScriptRoot '.nuget\plugins-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$dotnetPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = 'dotnet' }
& $dotnetPath run --project 'tests\Sf6Prototype.Checks' --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
& $dotnetPath publish 'src\Sf6Prototype\Sf6Prototype.csproj' -c Release -r win-x64 --self-contained false -o 'artifacts\app'
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Launch) { Start-Process -FilePath (Join-Path $PSScriptRoot 'artifacts\app\Sf6Prototype.exe') }
