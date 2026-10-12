param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src/Testumgebung.Desktop/Assets'
$msix = Join-Path $assets 'Msix'
New-Item -ItemType Directory -Force $msix | Out-Null
Add-Type -AssemblyName System.Drawing.Common
$source = [Drawing.Bitmap]::new((Join-Path $assets 'Testumgebung-icon-master.png'))
function Resize-Icon([int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($source, [Drawing.Rectangle]::new(0,0,$size,$size), 0,0,$source.Width,$source.Height,[Drawing.GraphicsUnit]::Pixel,$attributes)
        return $bitmap
    } finally { $attributes.Dispose(); $graphics.Dispose() }
}
function Save-Png([string]$path, [int]$size) {
    $bitmap = Resize-Icon $size
    try { $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
try {
    Save-Png (Join-Path $assets 'AppIcon.png') 256
    foreach ($asset in @(@('StoreLogo',50), @('Square44x44Logo',44), @('Square150x150Logo',150))) {
        Save-Png (Join-Path $msix ($asset[0]+'.png')) $asset[1]
        foreach ($scale in @(100,125,150,200,400)) {
            Save-Png (Join-Path $msix ($asset[0]+'.scale-'+$scale+'.png')) ([int][Math]::Round($asset[1]*$scale/100))
        }
    }
    $sizes = @(16,24,32,48,64,128,256)
    $frames = foreach ($size in $sizes) {
        Save-Png (Join-Path $msix ('Square44x44Logo.targetsize-'+$size+'_altform-unplated.png')) $size
        $bitmap = Resize-Icon $size
        $stream = [IO.MemoryStream]::new()
        try { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); ,$stream.ToArray() }
        finally { $stream.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $assets 'Testumgebung.ico'))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16*$sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $source.Dispose() }
Write-Host 'App-Icon und MSIX-Logos erstellt (16 bis 600 Pixel).'
