param([string]$PackageDirectory='artifacts\packages', [string]$ReleaseDirectory='artifacts\releases')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$packages=[IO.Path]::GetFullPath($PackageDirectory,$repoRoot)
$releases=[IO.Path]::GetFullPath($ReleaseDirectory,$repoRoot)
function Assert-Package([bool]$Condition,[string]$Message) {
    if(-not $Condition) {throw $Message}
}
function Read-ZipText($Archive,[string]$Name) {
    $entry=$Archive.GetEntry($Name)
    Assert-Package ($null -ne $entry) "Missing package entry: $Name"
    $reader=[IO.StreamReader]::new($entry.Open())
    try {return $reader.ReadToEnd()} finally {$reader.Dispose()}
}
$versions=@()
foreach($variant in @('self-contained','framework-dependent')) {
    $frameworkDependent=$variant -eq 'framework-dependent'
    $channel=if($frameworkDependent){'win-framework-dependent'}else{'win'}
    $feed=Get-Content -LiteralPath (Join-Path $releases "releases.$channel.json") -Raw | ConvertFrom-Json
    $assets=@($feed.Assets)
    Assert-Package ($assets.Count -eq 1 -and $assets[0].Type -eq 'Full') "Expected one full package in $channel feed."
    $asset=$assets[0]
    Assert-Package ($asset.PackageId -eq 'SF6Recorder' -and $asset.Version -match '^\d+\.\d+\.\d+$') "Invalid package identity in $channel feed."
    $versions+=$asset.Version
    $suffix=if($frameworkDependent){"-$channel"}else{''}
    Assert-Package ($asset.FileName -ceq "SF6Recorder-$($asset.Version)$suffix-full.nupkg") "Package filename does not match $channel."
    $full=Get-Item -LiteralPath (Join-Path $releases $asset.FileName)
    Assert-Package ($full.Length -eq $asset.Size -and (Get-FileHash -LiteralPath $full.FullName -Algorithm SHA256).Hash -eq $asset.SHA256) "Checksum mismatch in $channel feed."
    $portable=Join-Path $releases "SF6Recorder-$channel-Portable.zip"
    $download=Join-Path $packages "SF6-Recorder-win-x64-$variant.zip"
    Assert-Package ((Get-FileHash -LiteralPath $portable).Hash -eq (Get-FileHash -LiteralPath $download).Hash) "Download ZIP does not match $channel portable package."
    foreach($path in @($full.FullName,$portable)) {
        $archive=[IO.Compression.ZipFile]::OpenRead($path)
        try {
            $prefix=if($path.EndsWith('.nupkg')){'lib/app/'}else{'current/'}
            Assert-Package (@($archive.Entries | Where-Object {$_.FullName -match '(^|/)Data/'}).Count -eq 0) 'User data must not be packaged.'
            $manifests=@($prefix+'sq.version')
            if($path.EndsWith('.nupkg')) {$manifests+='SF6Recorder.nuspec'}
            foreach($name in $manifests) {
                [xml]$manifest=Read-ZipText $archive $name
                $metadata=$manifest.package.metadata
                Assert-Package ($metadata.id -eq 'SF6Recorder' -and $metadata.version -eq $asset.Version -and $metadata.channel -ceq $channel -and $metadata.rid -eq 'win-x64') "Manifest does not match $channel feed."
                Assert-Package ([string]::IsNullOrWhiteSpace($metadata.runtimeDependencies) -ne $frameworkDependent) "Incorrect runtime prerequisite declaration for $channel."
            }
            $configuration=Read-ZipText $archive ($prefix+'SF6 Recorder.runtimeconfig.json') | ConvertFrom-Json
            $options=$configuration.runtimeOptions
            if($frameworkDependent) {
                Assert-Package ($options.frameworks.name -contains 'Microsoft.WindowsDesktop.App' -and $null -eq $options.includedFrameworks) "Expected shared runtime configuration in $channel."
            } else {
                Assert-Package ($options.includedFrameworks.name -contains 'Microsoft.WindowsDesktop.App' -and $null -eq $options.frameworks) "Expected bundled runtime configuration in $channel."
            }
            foreach($runtimeFile in @('coreclr.dll','System.Private.CoreLib.dll','hostfxr.dll')) {
                Assert-Package (($null -ne $archive.GetEntry($prefix+$runtimeFile)) -ne $frameworkDependent) "Unexpected runtime file state in $channel`: $runtimeFile"
            }
        } finally {$archive.Dispose()}
    }
}
Assert-Package ($versions[0] -eq $versions[1]) 'Runtime variants must publish the same application version.'
Write-Host "Verified both update channels for $($versions[0]): feeds, checksums, manifests, dependencies, and runtime contents."
