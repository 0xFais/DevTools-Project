$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path $PSScriptRoot '../src/DevTools.App/Assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

function New-LogoBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 512.0, $size / 512.0)
    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(12,12,208,208,180,90); $tile.AddArc(292,12,208,208,270,90)
    $tile.AddArc(292,292,208,208,0,90); $tile.AddArc(12,292,208,208,90,90); $tile.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#141720'))
    $graphics.FillPath($background,$tile)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(120,90),[System.Drawing.Point]::new(400,420),[System.Drawing.ColorTranslator]::FromHtml('#DDD6FF'),[System.Drawing.ColorTranslator]::FromHtml('#8C73F5'))
    $outline = [System.Drawing.Pen]::new($gradient,25)
    $outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $diamond = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(256,84),[System.Drawing.PointF]::new(428,256),[System.Drawing.PointF]::new(256,428),[System.Drawing.PointF]::new(84,256))
    $graphics.DrawPolygon($outline,$diamond)
    $ink = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#F1EEFF'),22)
    $ink.StartCap = $ink.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ink.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $slash = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#B4A3FF'),18)
    $slash.StartCap = $slash.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    if ($size -le 24) {
        $core = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F1EEFF'))
        $graphics.FillPolygon($core,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(256,206),[System.Drawing.PointF]::new(306,256),[System.Drawing.PointF]::new(256,306),[System.Drawing.PointF]::new(206,256)))
        $core.Dispose()
    } else {
        $graphics.DrawLines($ink,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(214,202),[System.Drawing.PointF]::new(160,256),[System.Drawing.PointF]::new(214,310)))
        $graphics.DrawLines($ink,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(298,202),[System.Drawing.PointF]::new(352,256),[System.Drawing.PointF]::new(298,310)))
        $graphics.DrawLine($slash,274,220,238,292)
    }
    $slash.Dispose(); $ink.Dispose(); $outline.Dispose(); $gradient.Dispose(); $background.Dispose(); $tile.Dispose(); $graphics.Dispose()
    return ,$bitmap
}

$logo = New-LogoBitmap 512
$logo.Save((Join-Path $assetDir 'DevTools.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $bitmap = New-LogoBitmap $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size=$size; Bytes=$stream.ToArray() }
    $stream.Dispose(); $bitmap.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $assetDir 'DevTools.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output 'Generated DevTools PNG and nine-size Windows ICO.'
