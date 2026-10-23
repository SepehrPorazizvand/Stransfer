[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$SourceImage
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetsDirectory = Join-Path $projectRoot 'src\Stransfer.App\Assets'
New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null

$userImage = [System.IO.Path]::GetFullPath($SourceImage)

if (Test-Path -LiteralPath $userImage) {
    $src = [System.Drawing.Bitmap]::new($userImage)
    
    # 1. Extract the squircle icon (left side of user image, X=94..430, Y=118..453, size 336x336)
    $masterTile = [System.Drawing.Bitmap]::new(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($masterTile)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    
    # Clip to rounded rectangle (corner radius 110 on 512x512 tile)
    $radius = 110
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $r = [System.Drawing.Rectangle]::new(0, 0, 512, 512)
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    
    $g.SetClip($path)
    $srcRect = [System.Drawing.Rectangle]::new(94, 118, 336, 336)
    $destRect = [System.Drawing.Rectangle]::new(0, 0, 512, 512)
    $g.DrawImage($src, $destRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.ResetClip()
    $path.Dispose()
    $g.Dispose()

    # Save high-res app.png (512x512)
    $masterTile.Save((Join-Path $assetsDirectory 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png)

    # 2. Extract brand banner (right side: logo + Stransfer text)
    $brandW = 390
    $brandH = 340
    $brand = [System.Drawing.Bitmap]::new($brandW, $brandH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gb = [System.Drawing.Graphics]::FromImage($brand)
    $gb.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gb.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gb.Clear([System.Drawing.Color]::FromArgb(255, 20, 23, 32))
    $gb.DrawImage($src, [System.Drawing.Rectangle]::new(0, 0, $brandW, $brandH), [System.Drawing.Rectangle]::new(570, 115, $brandW, $brandH), [System.Drawing.GraphicsUnit]::Pixel)
    $gb.Dispose()
    $brand.Save((Join-Path $assetsDirectory 'brand.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $brand.Dispose()

    # 3. Generate multi-resolution app.ico
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $encodedImages = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($size in $sizes) {
        $resized = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gr = [System.Drawing.Graphics]::FromImage($resized)
        $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gr.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $gr.Clear([System.Drawing.Color]::Transparent)
        $gr.DrawImage($masterTile, 0, 0, $size, $size)
        $gr.Dispose()

        $stream = [System.IO.MemoryStream]::new()
        try {
            $resized.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $encodedImages.Add($stream.ToArray())
        }
        finally {
            $resized.Dispose()
            $stream.Dispose()
        }
    }

    $iconStream = [System.IO.File]::Create((Join-Path $assetsDirectory 'app.ico'))
    $writer = [System.IO.BinaryWriter]::new($iconStream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $entrySize = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$entrySize)
            $writer.Write([byte]$entrySize)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$encodedImages[$i].Length)
            $writer.Write([UInt32]$offset)
            $offset += $encodedImages[$i].Length
        }
        foreach ($encodedImage in $encodedImages) { $writer.Write([byte[]]$encodedImage) }
    }
    finally {
        $writer.Dispose()
        $masterTile.Dispose()
        $src.Dispose()
    }
    Write-Host "Generated app.ico, app.png, brand.png from user logo successfully." -ForegroundColor Green
}
