param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference='Stop'
$release=gh api "repos/{owner}/{repo}/releases/tags/$Tag"
if($LASTEXITCODE -ne 0) {throw 'Could not read the release assets.'}
$existing=@{}
foreach($file in ($release | ConvertFrom-Json).assets) {$existing[$file.name]=$file}

$packages=@()
$feeds=@()
foreach($variant in @('self-contained','framework-dependent')) {
    $zip="SF6-Recorder-win-x64-$variant.zip"
    if(-not $existing.ContainsKey($zip)) {$packages+="artifacts/packages/$zip"}
    $channel=if($variant -eq 'self-contained'){'win'}else{'win-framework-dependent'}
    $feed="releases.$channel.json"
    $feedPath="artifacts/releases/$feed"
    $package=(Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json).Assets[0]
    if($Tag -cne "v$($package.Version)") {throw "$feed does not match $Tag."}
    $name=$package.FileName
    if($existing.ContainsKey($feed)) {
        if(-not $existing.ContainsKey($name)) {throw "Published feed $feed is missing its package $name."}
        continue
    }
    if(-not $existing.ContainsKey($name)) {
        $packages+="artifacts/releases/$name"
    } elseif($existing[$name].digest -ne "sha256:$($package.SHA256)") {
        throw "Cannot publish ${feed}: the existing $name has a different checksum."
    }
    $feeds+=$feedPath
}
if($packages.Count) {
    gh release upload $Tag @packages
    if($LASTEXITCODE -ne 0) {throw 'Package upload failed; feeds were not published.'}
}
if($feeds.Count) {
    gh release upload $Tag @feeds
    if($LASTEXITCODE -ne 0) {throw 'Update feed upload failed.'}
}
