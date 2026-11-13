param([string]$OutputDirectory='artifacts\packages')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory,$repoRoot)
$stagingRoot=Join-Path $repoRoot '.local\package-staging'
$staging=Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output,$staging -Force | Out-Null
try {
    foreach($variant in @('self-contained','framework-dependent')) {
        $packed=Join-Path $staging $variant
        & (Join-Path $PSScriptRoot 'Publish-Update.ps1') -OutputDirectory $packed -FrameworkDependent:($variant -eq 'framework-dependent')
        $zip=Join-Path $packed 'SF6Recorder-win-Portable.zip'
        Copy-Item -LiteralPath $zip -Destination (Join-Path $output "SF6-Recorder-win-x64-$variant.zip") -Force
        if($variant -eq 'self-contained') {
            $feedOutput=Join-Path $repoRoot 'artifacts\releases'
            New-Item -ItemType Directory -Path $feedOutput -Force | Out-Null
            Get-ChildItem -LiteralPath $packed -File | Copy-Item -Destination $feedOutput -Force
        }
    }
} finally {
    $resolved=[IO.Path]::GetFullPath($staging)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($stagingRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unexpected package staging path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
