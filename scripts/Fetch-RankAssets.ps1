param([string]$SettingsPath = (Join-Path $PSScriptRoot '..\artifacts\app\Data\settings.json'))
$ErrorActionPreference = 'Stop'
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Sf6Prototype\Assets\Ranks'))
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$settings = Get-Content -LiteralPath $SettingsPath | ConvertFrom-Json
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $false
$handler.UseProxy = $settings.UseProxy
if ($settings.UseProxy) { $handler.Proxy = [Net.WebProxy]::new($settings.ProxyAddress) }
$client = [Net.Http.HttpClient]::new($handler)
$client.DefaultRequestVersion = [Version]::new(2,0)
$client.DefaultVersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionOrLower
$client.Timeout = [TimeSpan]::FromSeconds(25)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36')
$client.DefaultRequestHeaders.Referrer = [Uri]'https://www.streetfighter.com/6/buckler/'
$manifest = @()
try {
    # Official league IDs and the Master badges used by the website's rank component.
    # 39 is its explicit no-badge / unranked sentinel.
    foreach ($rankId in (@(1..37) + @(40..42))) {
        $name = "rank${rankId}_s.png"
        $uri = "https://www.streetfighter.com/6/buckler/assets/images/material/rank/$name"
        $destination = Join-Path $outputDirectory $name
        if (-not (Test-Path -LiteralPath $destination)) {
            $response = $client.GetAsync($uri).GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -eq [Net.HttpStatusCode]::NotFound) { continue }
                $null = $response.EnsureSuccessStatusCode()
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                if ($response.Content.Headers.ContentType.MediaType -ne 'image/png' -or
                    $bytes.Length -lt 24 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A') {
                    throw "Unexpected rank image: $name"
                }
                [IO.File]::WriteAllBytes($destination,$bytes)
            } finally { $response.Dispose(); Start-Sleep -Milliseconds 1000 }
        }
        $manifest += [ordered]@{ id=$rankId; file=$name; source=$uri; sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
        "Saved $name"
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputDirectory 'manifest.json') -Encoding utf8
} finally { $client.Dispose() }
