# 发布打包脚本：生成可分发到其他电脑的免安装绿色版
#
# 为什么需要它：dotnet publish 的产物会带上一些开发期不需要的东西，
# 直接压缩发给用户会显得臃肿或产生困惑。本地化资源是主要来源——
# WPF 会为十几种语言各生成一套附属程序集，合计约 10MB，
# 而本程序界面完全由自己绘制、文案全部为中文，用不到它们。
#
# 前提：必须用自包含方式发布。用户机器上通常没有 .NET 10 桌面运行时，
# 框架依赖型产物拷过去是打不开的。

param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    # 可选：显式指定 dotnet 路径。不传则自动探测。
    [string]$Dotnet = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\QYPlayer.App\QYPlayer.App.csproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactsRoot "QYPlayer-$Version-$Runtime"

# 定位 dotnet：优先用命令行传入的，其次 PATH，最后退回用户级安装位置。
# 不写死路径，换台机器也能直接跑。
$dotnet = $Dotnet
if (-not $dotnet) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
}
if (-not $dotnet) {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        'C:\Program Files\dotnet\dotnet.exe'
    )
    $dotnet = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $dotnet -or -not (Test-Path $dotnet)) {
    throw '未找到 dotnet。请安装 .NET 10 SDK、把它加入 PATH，或用 -Dotnet 指定完整路径。'
}
Write-Host "==> 使用 dotnet：$dotnet" -ForegroundColor DarkGray

Write-Host "==> 清理旧产物" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

Write-Host "==> 发布 $Version ($Runtime, 自包含)" -ForegroundColor Cyan
& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "发布失败，退出码 $LASTEXITCODE"
}

# 只保留简体中文与英文的附属资源，其余语言目录删掉。
# 这两个是 WPF 内建控件（消息框按钮、右键菜单等）可能用到的，
# 留着可以避免系统对话框出现英文/中文混排；其余语言纯属浪费体积。
Write-Host "==> 精简本地化资源（保留 zh-Hans / zh-Hant / en）" -ForegroundColor Cyan
$keep = @('zh-Hans', 'zh-Hant', 'en')
Get-ChildItem $publishDir -Directory | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $keep -notcontains $_.Name
} | ForEach-Object {
    Write-Host "    删除 $($_.Name)"
    Remove-Item -Recurse -Force $_.FullName
}

# 抹掉本机试运行留下的痕迹：这些目录程序首次启动会自己重建。
# 带进去会让用户以为拿到了一个"用过"的包，也可能残留本机路径。
Write-Host "==> 清理本机试运行痕迹" -ForegroundColor Cyan
foreach ($dir in @('data', 'portable')) {
    $path = Join-Path $publishDir $dir
    if (Test-Path $path) {
        Write-Host "    删除 $dir"
        Remove-Item -Recurse -Force $path
    }
}

# 删掉 C++ 链接用的导入库（.lib），运行时只需要 .dll。
# 这是给 C++ 项目编译期链接使用的，C# 程序完全用不到。
Write-Host "==> 移除 C++ 链接库（.lib）" -ForegroundColor Cyan
Get-ChildItem (Join-Path $publishDir 'libvlc') -Recurse -File -Filter '*.lib' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    删除 $($_.Name)"
    Remove-Item -Force $_.FullName
}

Write-Host "==> 确保插件目录存在" -ForegroundColor Cyan
$pluginsDir = Join-Path $publishDir 'plugins'
if (-not (Test-Path $pluginsDir)) {
    New-Item -ItemType Directory -Path $pluginsDir | Out-Null
}

# 打包成 zip 便于传输。
# 优先用 7-Zip：PowerShell 的 Compress-Archive 在 Windows PowerShell 5 下
# 会把条目路径写成反斜杠，不符合 ZIP 规范，部分第三方解压工具会解出
# 一堆名字带反斜杠的怪文件。7z 输出的是标准分隔符。
Write-Host "==> 生成压缩包" -ForegroundColor Cyan
$zipPath = Join-Path $artifactsRoot "QYPlayer-$Version-$Runtime.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

# 先看 PATH：7-Zip 未必装在默认位置，绿色版/包管理器安装的都只在 PATH 上。
$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) {
    $sevenZip = @(
        'C:\Program Files\7-Zip\7z.exe',
        'C:\Program Files (x86)\7-Zip\7z.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ($sevenZip) {
    Write-Host "    使用 7-Zip：$sevenZip" -ForegroundColor DarkGray
    # a = 添加；-tzip 指定格式；-mx=9 最高压缩率。
    # 注意传入 "$publishDir\*" 而非目录本身，避免压缩包里多套一层同名文件夹。
    & $sevenZip a -tzip -mx=9 $zipPath "$publishDir\*" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip 压缩失败，退出码 $LASTEXITCODE"
    }
} else {
    Write-Warning "未找到 7-Zip，改用内置压缩。条目分隔符可能不符合规范。"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
}

# 输出最终报告，包含体积与关键文件校验，避免"看起来成功其实缺文件"。
Write-Host ""
Write-Host "==> 完成" -ForegroundColor Green
$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
$zipMb = [math]::Round(((Get-Item $zipPath).Length / 1MB), 1)
Write-Host "    目录：$publishDir  ($sizeMb MB)"
Write-Host "    压缩：$zipPath  ($zipMb MB)"
Write-Host ""

$required = @(
    'QYPlayer.exe',
    'QYPlayer.dll',
    'QYPlayer.Core.dll',
    'QYPlayer.Audio.dll',
    'QYPlayer.Metadata.dll',
    'libvlc\win-x64\libvlc.dll',
    'libvlc\win-x64\libvlccore.dll'
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $publishDir $_)) }
if ($missing) {
    Write-Host "!! 缺少关键文件：" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    exit 1
}
Write-Host "    关键文件校验通过（$($required.Count) 项）" -ForegroundColor Green
