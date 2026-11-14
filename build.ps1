param([switch]$Launch, [switch]$ChecksOnly)
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
& $dotnetPath run --project 'tests\Sf6Prototype.DesktopChecks' --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Headless desktop checks failed.' }
if ($ChecksOnly) { return }
& (Join-Path $PSScriptRoot 'scripts\Publish-Update.ps1') -OutputDirectory '.local\delivered-update' -FrameworkDependent
& (Join-Path $PSScriptRoot 'scripts\Install-PortableBuild.ps1') -Package '.local\delivered-update\SF6Recorder-win-framework-dependent-Portable.zip'
# Remove the former application identity after a successful publish; keep Data intact.
$legacyFiles = @('Sf6Prototype.exe', 'Sf6Prototype.dll', 'Sf6Prototype.pdb', 'Sf6Prototype.deps.json', 'Sf6Prototype.runtimeconfig.json')
$legacyFiles += @('ja', 'zh-Hans', 'zh-Hant') | ForEach-Object { "$_\Sf6Prototype.resources.dll" }
foreach ($relativePath in $legacyFiles) {
    $legacyPath = Join-Path $PSScriptRoot "artifacts\app\$relativePath"
    if (Test-Path -LiteralPath $legacyPath -PathType Leaf) { Remove-Item -LiteralPath $legacyPath }
}
if ($Launch) { Start-Process -FilePath (Join-Path $PSScriptRoot 'artifacts\app\SF6 Recorder.exe') }
