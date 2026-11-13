param([string]$OutputDirectory = 'artifacts\packages')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.local\dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repoRoot '.nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $repoRoot '.nuget\plugins-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$dotnetPath = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = 'dotnet' }

$outputPath = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
# Always publish into a new directory, never the delivered app or its Data folder.
$stagingRoot = Join-Path $repoRoot '.local\package-staging'
$stagingPath = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingPath | Out-Null
try {
    foreach ($variant in @('self-contained', 'framework-dependent')) {
        $selfContained = if ($variant -eq 'self-contained') { 'true' } else { 'false' }
        $publishPath = Join-Path $stagingPath $variant
        & $dotnetPath publish 'src\Sf6Prototype\Sf6Prototype.csproj' -c Release -r win-x64 --self-contained $selfContained -o $publishPath -p:DebugType=None -p:DebugSymbols=false -p:GenerateDocumentationFile=false
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $variant" }
        if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'SF6 Recorder.exe'))) {
            throw "Missing SF6 Recorder.exe: $variant"
        }
        $hasRuntime = Test-Path -LiteralPath (Join-Path $publishPath 'coreclr.dll')
        if ($hasRuntime -ne ($selfContained -eq 'true')) { throw "Unexpected runtime contents: $variant" }
        if (Test-Path -LiteralPath (Join-Path $publishPath 'Data')) { throw 'App data must never be packaged.' }
        Get-ChildItem -LiteralPath $publishPath -Recurse -File |
            Where-Object Extension -In @('.pdb', '.xml') | Remove-Item
        $zipName = "SF6-Recorder-win-x64-$variant.zip"
        $stagedZip = Join-Path $stagingPath $zipName
        [IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Copy-Item -LiteralPath $stagedZip -Destination (Join-Path $outputPath $zipName) -Force
        Write-Host "Created $(Join-Path $outputPath $zipName)"
    }
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($stagingPath)
    $expectedRoot = [IO.Path]::GetFullPath($stagingRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStaging.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a staging directory outside the package staging root.'
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}
