param([string]$Out = "docs\screenshots\screen.png")

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)

$out = Join-Path (Get-Location) $Out
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

# Downscale so the whole desktop stays viewable and cheap to inspect.
$scale = 0.55
$small = New-Object System.Drawing.Bitmap([int]($b.Width * $scale), [int]($b.Height * $scale))
$sg = [System.Drawing.Graphics]::FromImage($small)
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$sg.DrawImage($bmp, 0, 0, $small.Width, $small.Height)

$small.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose(); $small.Dispose(); $g.Dispose(); $bmp.Dispose()
Write-Output "SAVED $out ($($b.Width)x$($b.Height) -> $($small.Width)x$($small.Height))"
