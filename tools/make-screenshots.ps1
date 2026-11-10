# Renders the tray menu (English and Korean) and the icon states into ../docs.
# Run with: powershell -NoProfile -ExecutionPolicy Bypass -STA -File tools\make-screenshots.ps1
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$out = Join-Path $PSScriptRoot '..\docs'
New-Item -ItemType Directory -Force $out | Out-Null

# Items: '-' = separator, '!' prefix = disabled (status), '*' prefix = checked
function Save-Menu($items, $file) {
    $m = New-Object System.Windows.Forms.ContextMenuStrip
    foreach ($t in $items) {
        if ($t -eq '-') { [void]$m.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator)); continue }
        $it = New-Object System.Windows.Forms.ToolStripMenuItem($t.TrimStart('!', '*'))
        if ($t.StartsWith('!')) { $it.Enabled = $false }
        if ($t.StartsWith('*')) { $it.Checked = $true }
        [void]$m.Items.Add($it)
    }
    $m.Show(300, 300)
    [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 400
    $bmp = New-Object System.Drawing.Bitmap($m.Width, $m.Height)
    $m.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle(0, 0, $m.Width, $m.Height)))
    $m.Close()
    $big = New-Object System.Drawing.Bitmap(($bmp.Width * 2), ($bmp.Height * 2))
    $g = [System.Drawing.Graphics]::FromImage($big); $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($bmp, 0, 0, $big.Width, $big.Height); $g.Dispose()
    $big.Save((Join-Path $out $file), [System.Drawing.Imaging.ImageFormat]::Png)
}

# Labels must match KrakenAutoSwitch.cs (both dongles plugged in)
Save-Menu @('!Razer Kraken V4: on', '!Logitech G325: off', '-', 'Switch to Razer Kraken V4 now', 'Switch to Logitech G325 now', 'Restore previous device now', '-', '*Start with Windows', 'Open log', 'Exit') 'tray-menu.png'
Save-Menu @('!Razer Kraken V4: 켜짐', '!Logitech G325: 꺼짐', '-', 'Razer Kraken V4(으)로 지금 전환', 'Logitech G325(으)로 지금 전환', '이전 장치로 지금 복원', '-', '*Windows 시작 시 자동 실행', '로그 열기', '종료') 'tray-menu.ko.png'

# Icon states (same drawing as the app)
$ic = New-Object System.Drawing.Bitmap(540, 200)
$g = [System.Drawing.Graphics]::FromImage($ic)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'; $g.Clear([System.Drawing.Color]::White)
$f = New-Object System.Drawing.Font('Segoe UI', 12)
$items = @(@('LimeGreen', 'A headset is on'), @('Gray', 'All headsets off'), @('Orange', 'Unknown (until first event)'))
$x = 20
foreach ($i in $items) {
    $br = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromName($i[0]))
    $g.FillEllipse($br, $x + 30, 30, 90, 90)
    $g.DrawEllipse([System.Drawing.Pens]::Black, $x + 30, 30, 90, 90)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'
    $g.DrawString($i[1], $f, [System.Drawing.Brushes]::Black, ([System.Drawing.RectangleF]::new([single]($x - 5), 135, 160, 50)), $sf)
    $x += 170
}
$g.Dispose()
$ic.Save((Join-Path $out 'tray-icons.png'), [System.Drawing.Imaging.ImageFormat]::Png)
