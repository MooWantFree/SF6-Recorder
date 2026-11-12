# Regenerate the executable icon from the window's vector icon and theme colors.
# Run: pwsh -NoProfile -STA -File .\scripts\Generate-AppIcon.ps1
# Renders only to an in-memory bitmap; no application windows are created.
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this script with pwsh -STA.'
}
Add-Type -AssemblyName PresentationCore, PresentationFramework
$projectPath = Join-Path $PSScriptRoot '../src/Sf6Prototype'
[xml]$utility = Get-Content -LiteralPath (Join-Path $projectPath 'Themes/Utility.xaml') -Raw
[xml]$tokens = Get-Content -LiteralPath (Join-Path $projectPath 'Themes/Tokens.xaml') -Raw
$namespaces = [Xml.XmlNamespaceManager]::new($utility.NameTable)
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$iconMarkup = $utility.SelectSingleNode('//*[@x:Key="AppIcon"]', $namespaces).OuterXml
$tokenNamespaces = [Xml.XmlNamespaceManager]::new($tokens.NameTable)
$tokenNamespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$iconMarkup = [regex]::Replace($iconMarkup, '\{StaticResource (\w+)\}', {
    param($match)
    $color = $tokens.SelectSingleNode('//*[@x:Key="' + $match.Groups[1].Value + '"]', $tokenNamespaces).GetAttribute('Color')
    if (-not $color) { throw "Icon resource has no solid color: $($match.Value)" }
    return $color
})
$dictionaryMarkup = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + $iconMarkup + '</ResourceDictionary>'
$resources = [Windows.Markup.XamlReader]::Parse($dictionaryMarkup)
$icon = $resources['AppIcon']
$frames = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 128, 256) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 32.0, $size / 32.0))
    $drawing.DrawDrawing($icon.Drawing)
    $drawing.Pop()
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    } finally { $stream.Dispose() }
}
$assetPath = Join-Path $projectPath 'Assets'
[IO.Directory]::CreateDirectory($assetPath) | Out-Null
$outputPath = Join-Path $assetPath 'app.ico'
$writer = [IO.BinaryWriter]::new([IO.File]::Create($outputPath))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Output "Generated $outputPath ($($frames.Count) sizes, 16-256 px)."
