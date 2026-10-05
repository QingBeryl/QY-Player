# 生成应用图标。
#
# 为什么用脚本生成而不是直接放一张图：图标需要随品牌色调整，
# 二进制文件进版本库后无法审阅差异。脚本生成则改动可追溯、可复现。
#
# 产物：src\QYPlayer.App\Assets\app.ico
# 该文件被 QYPlayer.App.csproj 以 ApplicationIcon 引用，
# 决定了 exe、任务栏、开始菜单与安装程序的图标。

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'src\QYPlayer.App\Assets'
$icoPath = Join-Path $assetsDir 'app.ico'

if (-not (Test-Path $assetsDir)) {
    New-Item -ItemType Directory -Path $assetsDir | Out-Null
}

# 圆角方形底 + 白色播放三角，风格与主界面的深色主题一致。
# 只画一个大尺寸再交给系统缩放，比手绘多套像素尺寸更不容易失真。
$size = 256
$bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

# 圆角半径取 20%，接近 Windows 11 图标观感。
$radius = 52
$shape = New-Object System.Drawing.Drawing2D.GraphicsPath
$shape.AddArc(0, 0, $radius * 2, $radius * 2, 180, 90)
$shape.AddArc($size - $radius * 2, 0, $radius * 2, $radius * 2, 270, 90)
$shape.AddArc($size - $radius * 2, $size - $radius * 2, $radius * 2, $radius * 2, 0, 90)
$shape.AddArc(0, $size - $radius * 2, $radius * 2, $radius * 2, 90, 90)
$shape.CloseFigure()

$gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point($size, $size)),
    [System.Drawing.Color]::FromArgb(255, 109, 91, 255),
    [System.Drawing.Color]::FromArgb(255, 43, 210, 255))
$graphics.FillPath($gradient, $shape)

# 三角形刻意略微右移，纯几何居中在视觉上会显得偏左。
$triangle = @(
    (New-Object System.Drawing.PointF(104, 74)),
    (New-Object System.Drawing.PointF(104, 182)),
    (New-Object System.Drawing.PointF(192, 128))
)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$graphics.FillPolygon($white, [System.Drawing.PointF[]]$triangle)

$graphics.Dispose()

# 打包成 ICO。单个 256x256 的 PNG 条目即可，
# 其余尺寸由 Windows 自行缩放，这样脚本不必手写多套位图数据。
$pngStream = New-Object System.IO.MemoryStream
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$pngStream.Dispose()
$bitmap.Dispose()

$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($icoStream)
$writer.Write([UInt16]0)              # 保留字段
$writer.Write([UInt16]1)              # 类型：1 = 图标
$writer.Write([UInt16]1)              # 图像数量
$writer.Write([Byte]0)                # 宽度 0 表示 256
$writer.Write([Byte]0)                # 高度 0 表示 256
$writer.Write([Byte]0)                # 调色板数量
$writer.Write([Byte]0)                # 保留字段
$writer.Write([UInt16]1)              # 色彩平面数
$writer.Write([UInt16]32)             # 位深
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)             # 数据偏移：6 字节目录头 + 16 字节条目
$writer.Write($pngBytes)
$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $icoStream.ToArray())
$writer.Dispose()
$icoStream.Dispose()

Write-Host "已生成图标：$icoPath ($([math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB)"
