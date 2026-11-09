# Deterministic thumbnail: exact two-line wording, editable layout, no external fonts/assets.
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(1024, 1024)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#141820'))
$bubbleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#222B35'))
$textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F1F1EC'))
$border = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#778490'), 5)
$checkPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#A4C6AD'), 13)
$checkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$checkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$checkPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
# Rounded speech bubble with an integrated lower-left tail.
$path.AddArc(60, 246, 96, 96, 180, 90)
$path.AddArc(868, 246, 96, 96, 270, 90)
$path.AddArc(868, 634, 96, 96, 0, 90)
$path.AddLine(868, 730, 340, 730)
$path.AddLine(340, 730, 206, 822)
$path.AddLine(206, 822, 236, 730)
$path.AddLine(236, 730, 108, 730)
$path.AddArc(60, 634, 96, 96, 90, 90)
$path.CloseFigure()
$graphics.FillPath($bubbleBrush, $path)
$graphics.DrawPath($border, $path)
$graphics.DrawRectangle($border, 123, 436, 106, 106)
$graphics.DrawLines($checkPen, [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(145, 488),
    [System.Drawing.PointF]::new(168, 513),
    [System.Drawing.PointF]::new(210, 463)))
$font = [System.Drawing.Font]::new('Segoe UI', 78, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString('Only Put', $font, $textBrush, [System.Drawing.PointF]::new(270, 365))
$graphics.DrawString('Stolen Goods', $font, $textBrush, [System.Drawing.PointF]::new(270, 475))
$bitmap.Save((Join-Path $PSScriptRoot 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$encoding = [System.Drawing.Imaging.EncoderParameters]::new(1)
$encoding.Param[0] = [System.Drawing.Imaging.EncoderParameter]::new([System.Drawing.Imaging.Encoder]::Quality, [long]92)
$bitmap.Save((Join-Path $PSScriptRoot 'preview.jpg'), $codec, $encoding)
$encoding.Dispose()
$font.Dispose()
$path.Dispose()
$checkPen.Dispose()
$border.Dispose()
$textBrush.Dispose()
$bubbleBrush.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$thumbnail = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'preview.jpg')
if ($thumbnail.Length -ge 1MB) { throw 'Workshop thumbnail must be below 1 MB' }
Write-Output "Created 1024x1024 preview.png and preview.jpg ($($thumbnail.Length) JPEG bytes)."
