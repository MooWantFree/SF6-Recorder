param([string]$OutputDirectory='artifacts\releases', [switch]$FrameworkDependent, [string]$Version)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_HOME=Join-Path $repoRoot '.local\dotnet-cli'
$env:NUGET_PACKAGES=Join-Path $repoRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$dotnetPath=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $dotnetPath)) {$dotnetPath='dotnet'}
[xml]$project=Get-Content -LiteralPath 'src\Sf6Prototype\Sf6Prototype.csproj'
$toolVersion=($project.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Velopack').Version
if(-not $Version) {$Version=$project.Project.PropertyGroup.Version}
if($Version -notmatch '^\d+\.\d+\.\d+$') {throw 'A stable three-part version is required.'}
$toolDirectory=Join-Path $repoRoot '.tools\velopack'
$vpk=Join-Path $toolDirectory 'vpk.exe'
if(-not(Test-Path -LiteralPath $vpk)) {
    & $dotnetPath tool install vpk --tool-path $toolDirectory --version $toolVersion
    if($LASTEXITCODE -ne 0) {throw 'Velopack tool installation failed.'}
}
if(-not(Test-Path -LiteralPath (Join-Path $toolDirectory ".store\vpk\$toolVersion"))) {throw "Expected project-local vpk $toolVersion."}
$stagingRoot=Join-Path $repoRoot '.local\update-staging'
$staging=Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
$publish=Join-Path $staging 'publish'
$packed=Join-Path $staging 'packed'
$output=[IO.Path]::GetFullPath($OutputDirectory,$repoRoot)
New-Item -ItemType Directory -Path $publish,$packed,$output -Force | Out-Null
try {
    $selfContained=if($FrameworkDependent){'false'}else{'true'}
    & $dotnetPath publish 'src\Sf6Prototype\Sf6Prototype.csproj' -c Release -r win-x64 --self-contained $selfContained -o $publish "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false
    if($LASTEXITCODE -ne 0) {throw 'Update publish failed.'}
    if(Test-Path -LiteralPath (Join-Path $publish 'Data')) {throw 'User data must never be packaged.'}
    & $vpk pack --packId SF6Recorder --packTitle 'SF6 Recorder' --packAuthors MooWantFree --packVersion $Version --packDir $publish --mainExe 'SF6 Recorder.exe' --channel win --runtime win-x64 --delta None --noInst --icon 'src\Sf6Prototype\Assets\app.ico' --outputDir $packed
    if($LASTEXITCODE -ne 0) {throw 'Velopack packaging failed.'}
    foreach($file in Get-ChildItem -LiteralPath $packed -File) {Copy-Item -LiteralPath $file.FullName -Destination $output -Force}
    Write-Host "Update packages: $output"
} finally {
    $resolved=[IO.Path]::GetFullPath($staging)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($stagingRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unexpected staging directory.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
