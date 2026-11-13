param([Parameter(Mandatory)][string]$Package, [string]$Destination='artifacts\app')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$destinationPath=[IO.Path]::GetFullPath($Destination,$repoRoot)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))+'\'
if(-not $destinationPath.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) {throw 'Build delivery must stay inside artifacts.'}
$workRoot=Join-Path $repoRoot '.local\portable-delivery'
$work=Join-Path $workRoot ([Guid]::NewGuid().ToString('N'))
if(-not [IO.Path]::GetFullPath($work).StartsWith([IO.Path]::GetFullPath($workRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unexpected delivery staging directory.'}
$unpacked=Join-Path $work 'new'
$backup=Join-Path $work 'previous'
New-Item -ItemType Directory -Path $unpacked,$backup,$destinationPath -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Package,$repoRoot),$unpacked)
$owned=@('current','SF6 Recorder.exe','Update.exe','.portable')
foreach($name in $owned) {if(-not(Test-Path -LiteralPath (Join-Path $unpacked $name))) {throw "Incomplete portable bundle: $name"}}
if(Test-Path -LiteralPath (Join-Path $unpacked 'Data')) {throw 'Portable packages must not contain user data.'}
foreach($process in @(Get-Process -Name 'SF6 Recorder' -ErrorAction SilentlyContinue)) {
    if($process.Path.StartsWith($destinationPath+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Close the delivered application before installing a build.'}
}
$moved=[Collections.Generic.List[string]]::new()
$installed=[Collections.Generic.List[string]]::new()
try {
    foreach($name in $owned) {
        $old=Join-Path $destinationPath $name
        if(Test-Path -LiteralPath $old) {Move-Item -LiteralPath $old -Destination (Join-Path $backup $name);$moved.Add($name)}
        Move-Item -LiteralPath (Join-Path $unpacked $name) -Destination $old
        $installed.Add($name)
    }
} catch {
    foreach($name in $installed) {Move-Item -LiteralPath (Join-Path $destinationPath $name) -Destination (Join-Path $unpacked $name)}
    foreach($name in $moved) {Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $destinationPath $name)}
    throw
}
# Data, WebView2 and package state are never moved or overwritten. Retain the
# previous program files in .local for recovery rather than deleting user files.
Write-Host "Delivered portable build to $destinationPath; previous binaries: $backup"
