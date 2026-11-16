Add-Type -AssemblyName System.Drawing

function RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc(($x+$w-$d), $y, $d, $d, 270, 90)
    $path.AddArc(($x+$w-$d), ($y+$h-$d), $d, $d, 0, 90)
    $path.AddArc($x, ($y+$h-$d), $d, $d, 90, 90)
    $path.CloseFigure()
    return ,$path
}

$bitmap = New-Object System.Drawing.Bitmap 1024,1024
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.Clear([System.Drawing.Color]::Transparent)
$graphics.ScaleTransform(2,2)
$background = RoundedPath 20 20 472 472 112
$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(40,20)),([System.Drawing.Point]::new(470,500)),([System.Drawing.Color]::FromArgb(35,77,62)),([System.Drawing.Color]::FromArgb(13,34,29))
$graphics.FillPath($gradient, $background)
$border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(75,133,109)),2
$graphics.DrawPath($border,$background)
$amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(247,186,76))
$mint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(207,237,216))
$source = RoundedPath 83 124 134 134 28
$destination = RoundedPath 295 254 134 134 28
$sourcePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(207,237,216)),19
$graphics.DrawPath($sourcePen,$source)
$graphics.FillPath($amber,$destination)
# A right-angle route ending in a strong arrow, visible without lettering.
$route = New-Object System.Drawing.Drawing2D.GraphicsPath
$route.AddLines([System.Drawing.PointF[]]@([System.Drawing.PointF]::new(150,283),[System.Drawing.PointF]::new(150,321),[System.Drawing.PointF]::new(257,321)))
$routePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(247,186,76)),22
$routePen.StartCap = 'Round'
$routePen.EndCap = 'Round'
$routePen.LineJoin = 'Round'
$graphics.DrawPath($routePen,$route)
$graphics.FillPolygon($amber,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(246,290),[System.Drawing.PointF]::new(280,321),[System.Drawing.PointF]::new(246,352)))
$graphics.FillEllipse($mint,136,177,28,28)
$dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(25,55,43))
$graphics.FillEllipse($dark,348,307,28,28)
$graphics.Dispose()
$bitmap.Save((Join-Path $PSScriptRoot '程序联动助手图标.png'),[System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16,20,24,32,40,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $small = New-Object System.Drawing.Bitmap $size,$size
    $canvas = [System.Drawing.Graphics]::FromImage($small)
    $canvas.InterpolationMode = 'HighQualityBicubic'
    $canvas.PixelOffsetMode = 'HighQuality'
    $canvas.DrawImage($bitmap,0,0,$size,$size)
    $memory = New-Object System.IO.MemoryStream
    $small.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,$memory.ToArray()
    $canvas.Dispose(); $small.Dispose(); $memory.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path $PSScriptRoot '程序联动助手.ico'))
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $bitmap.Dispose()
foreach ($resource in @($background,$gradient,$border,$amber,$mint,$source,$destination,$sourcePen,$route,$routePen,$dark)) { $resource.Dispose() }
