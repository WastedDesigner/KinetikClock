# Draws icon.ico: a dark rounded tile with a teal clock face, light hour hand and red minute hand.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Png([int]$s) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $s * 0.06); $w = $s - 2 * $m; $r = $s * 0.2
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 34, 34, 40))), (RoundRect $m $m $w $w $r))
    $cx = $s / 2; $cy = $s / 2; $R = $w * 0.36
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 40, 190, 200)), ([Math]::Max(1, $s * 0.07))
    $g.DrawEllipse($ring, [float]($cx - $R), [float]($cy - $R), [float](2 * $R), [float](2 * $R))
    if ($s -ge 32) { $tick = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 220, 220, 228)); $t = $s * 0.035
        for ($i = 0; $i -lt 12; $i += 3) { $a = $i * [Math]::PI / 6; $x = $cx + [Math]::Sin($a) * $R * 0.78; $y = $cy - [Math]::Cos($a) * $R * 0.78
            $g.FillEllipse($tick, [float]($x - $t), [float]($y - $t), [float](2 * $t), [float](2 * $t)) } }
    $cap = [System.Drawing.Drawing2D.LineCap]::Round
    $hp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 220, 220, 228)), ([Math]::Max(1, $s * 0.075)); $hp.StartCap = $cap; $hp.EndCap = $cap
    $g.DrawLine($hp, [float]$cx, [float]$cy, [float]($cx - $R * 0.42), [float]($cy - $R * 0.30))
    $mp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 240, 88, 88)), ([Math]::Max(1, $s * 0.055)); $mp.StartCap = $cap; $mp.EndCap = $cap
    $g.DrawLine($mp, [float]$cx, [float]$cy, [float]($cx + $R * 0.18), [float]($cy - $R * 0.66))
    $d = $s * 0.05; $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 240, 88, 88))), [float]($cx - $d), [float]($cy - $d), [float](2 * $d), [float](2 * $d))
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$imgs = $sizes | ForEach-Object { , (Png $_) }
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $imgs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $imgs) { $bw.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $out.ToArray())
Write-Output "Wrote icon.ico"

