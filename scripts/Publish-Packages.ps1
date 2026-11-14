param([string]$OutputDirectory='artifacts\packages', [string]$UpdateOutputDirectory='artifacts\releases', [string]$Version)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory,$repoRoot)
$feedOutput=[IO.Path]::GetFullPath($UpdateOutputDirectory,$repoRoot)
$stagingRoot=Join-Path $repoRoot '.local\package-staging'
$staging=Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output,$feedOutput,$staging -Force | Out-Null
try {
    foreach($variant in @('self-contained','framework-dependent')) {
        $packed=Join-Path $staging $variant
        $frameworkDependent=$variant -eq 'framework-dependent'
        $channel=if($frameworkDependent){'win-framework-dependent'}else{'win'}
        & (Join-Path $PSScriptRoot 'Publish-Update.ps1') -OutputDirectory $packed -FrameworkDependent:$frameworkDependent -Version $Version
        $zip=Join-Path $packed "SF6Recorder-$channel-Portable.zip"
        Copy-Item -LiteralPath $zip -Destination (Join-Path $output "SF6-Recorder-win-x64-$variant.zip") -Force
        Get-ChildItem -LiteralPath $packed -File | Copy-Item -Destination $feedOutput -Force
    }
    & (Join-Path $PSScriptRoot 'Test-UpdatePackages.ps1') -PackageDirectory $output -ReleaseDirectory $feedOutput
} finally {
    $resolved=[IO.Path]::GetFullPath($staging)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($stagingRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unexpected package staging path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
