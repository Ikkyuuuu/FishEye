# Regenerates the header PNG and multi-resolution Windows icon from logo.svg.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$branding = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\branding'
[xml]$svg = Get-Content -LiteralPath (Join-Path $branding 'logo.svg') -Raw
$accent = [Drawing.ColorTranslator]::FromHtml($svg.svg.stroke)

function New-LogoBitmap([int]$Size, [bool]$WithBackground) {
    $work = [Drawing.Bitmap]::new($Size * 4, $Size * 4)
    $graphics = [Drawing.Graphics]::FromImage($work)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($Size * 4 / 64.0, $Size * 4 / 64.0)
    if ($WithBackground) {
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddArc(0, 0, 28, 28, 180, 90)
        $path.AddArc(36, 0, 28, 28, 270, 90)
        $path.AddArc(36, 36, 28, 28, 0, 90)
        $path.AddArc(0, 36, 28, 28, 90, 90)
        $path.CloseFigure()
        $background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(20, 24, 30))
        $graphics.FillPath($background, $path)
        $background.Dispose(); $path.Dispose()
    }
    $pen = [Drawing.Pen]::new($accent, [single]::Parse($svg.svg.'stroke-width', [Globalization.CultureInfo]::InvariantCulture))
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $brush = [Drawing.SolidBrush]::new($accent)
    foreach ($element in $svg.svg.ChildNodes) {
        switch ($element.LocalName) {
            'polygon' {
                [Drawing.PointF[]]$points = @($element.points.Split(' ') | ForEach-Object {
                    $xy = $_.Split(','); [Drawing.PointF]::new([single]$xy[0], [single]$xy[1])
                })
                $graphics.DrawPolygon($pen, $points)
            }
            'ellipse' {
                $graphics.DrawEllipse($pen, [single]($element.cx - $element.rx), [single]($element.cy - $element.ry), [single](2 * $element.rx), [single](2 * $element.ry))
            }
            'circle' {
                $radius = [single]::Parse($element.r, [Globalization.CultureInfo]::InvariantCulture)
                $graphics.FillEllipse($brush, [single]($element.cx - $radius), [single]($element.cy - $radius), 2 * $radius, 2 * $radius)
            }
        }
    }
    $brush.Dispose(); $pen.Dispose(); $graphics.Dispose()
    $result = [Drawing.Bitmap]::new($Size, $Size)
    $downsample = [Drawing.Graphics]::FromImage($result)
    $downsample.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $downsample.DrawImage($work, 0, 0, $Size, $Size)
    $downsample.Dispose(); $work.Dispose()
    return $result
}

$logo = New-LogoBitmap 256 $false
$logo.Save((Join-Path $branding 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-LogoBitmap $size $true
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $branding 'app-icon.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose(); $stream.Dispose()
}
$file = [IO.File]::Create((Join-Path $branding 'FishEyes.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose(); $file.Dispose() }
Write-Host 'Generated FishEyes logo and Windows icon.'
