# 发布打包脚本：生成可分发到其他电脑的绿色便捷版与安装版
#
# 为什么需要它：dotnet publish 的产物会带上一些开发期不需要的东西，
# 直接压缩发给用户会显得臃肿或产生困惑。本地化资源是主要来源——
# WPF 会为十几种语言各生成一套附属程序集，合计约 10MB，
# 而本程序界面完全由自己绘制、文案全部为中文，用不到它们。
#
# 产出（均落在 dist\v<版本>\，按版本归档，历史版本不覆盖）：
#   QYPlayer-<版本>-win-x64.zip        便捷版：解压即用，不写注册表
#   QYPlayer-<版本>-win-x64-setup.exe  安装版：带向导、开始菜单与卸载
#
# 前提：必须用自包含方式发布。用户机器上通常没有 .NET 10 桌面运行时，
# 框架依赖型产物拷过去是打不开的。

param(
    # 留空则从 Directory.Build.props 读，保证版本号只有一处来源。
    [string]$Version = '',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    # 可选：显式指定 dotnet 路径。不传则自动探测。
    [string]$Dotnet = '',
    # 可选：显式指定 Inno Setup 编译器路径。不传则自动探测。
    [string]$Iscc = '',
    # 只打包某一项：all / portable / setup。
    [ValidateSet('all', 'portable', 'setup')]
    [string]$Target = 'all'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\QYPlayer.App\QYPlayer.App.csproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$distRoot = Join-Path $repoRoot 'dist'

# 版本号从 Directory.Build.props 抓取，避免在脚本与工程文件里各写一份。
# 不用 XML 解析是为了保持脚本对文件格式的小改动不敏感。
if (-not $Version) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $propsText = Get-Content $propsPath -Raw
    if ($propsText -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
    } else {
        throw "未能从 $propsPath 读取 <Version>，请检查文件内容。"
    }
}
Write-Host "==> 版本：$Version" -ForegroundColor Cyan

$publishDir = Join-Path $artifactsRoot "QYPlayer-$Version-$Runtime"
$distDir = Join-Path $distRoot "v$Version"

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

# ---------------------------------------------------------------------------
# 生成图标（若缺失）。图标是安装程序与 exe 的资源，必须先于发布存在。
# ---------------------------------------------------------------------------
$iconPath = Join-Path $repoRoot 'src\QYPlayer.App\Assets\app.ico'
if (-not (Test-Path $iconPath)) {
    Write-Host "==> 生成应用图标" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-icon.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "生成图标失败，退出码 $LASTEXITCODE"
    }
}

# ---------------------------------------------------------------------------
# 发布
# ---------------------------------------------------------------------------
$needPublish = $Target -ne 'setup'
if ($needPublish) {
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
} elseif (-not (Test-Path $publishDir)) {
    # 只做安装版时必须已有发布目录，否则无从打包。
    throw "未找到发布目录 $publishDir，请先去掉 -Target setup 跑一次完整打包。"
}

# ---------------------------------------------------------------------------
# 按版本归档。历史版本的产物一律保留，方便回溯与对照。
# ---------------------------------------------------------------------------
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
    Write-Host "==> 新建版本目录 $distDir" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 便捷版：zip 压缩包
# ---------------------------------------------------------------------------
if ($Target -in @('all', 'portable')) {
    # 优先用 7-Zip：PowerShell 的 Compress-Archive 在 Windows PowerShell 5 下
    # 会把条目路径写成反斜杠，不符合 ZIP 规范，部分第三方解压工具会解出
    # 一堆名字带反斜杠的怪文件。7z 输出的是标准分隔符。
    Write-Host "==> 生成便捷版压缩包" -ForegroundColor Cyan
    $zipPath = Join-Path $distDir "QYPlayer-$Version-$Runtime.zip"
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

    $zipMb = [math]::Round(((Get-Item $zipPath).Length / 1MB), 1)
    Write-Host "    $zipPath  ($zipMb MB)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 安装版：Inno Setup 编译出单个 setup.exe
# ---------------------------------------------------------------------------
if ($Target -in @('all', 'setup')) {
    Write-Host "==> 生成安装版" -ForegroundColor Cyan

    # 定位 ISCC：命令行参数 → PATH → 常见安装位置 → 本机工作目录下的绿色副本。
    # 最后一项是本项目构建环境的事实约定，便于在未装 Inno Setup 的机器上跑通。
    $isccExe = $Iscc
    if (-not $isccExe) {
        $isccExe = (Get-Command ISCC -ErrorAction SilentlyContinue).Source
    }
    if (-not $isccExe) {
        $isccExe = @(
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path $env:USERPROFILE '.trae-cn\work\6ac3925dc68b84257cb1053c\innosetup\app\ISCC.exe')
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $isccExe) {
        throw '未找到 Inno Setup 编译器 ISCC.exe。请安装 Inno Setup 6，或用 -Iscc 指定完整路径。'
    }
    Write-Host "    使用 ISCC：$isccExe" -ForegroundColor DarkGray

    $setupPath = Join-Path $distDir "QYPlayer-$Version-$Runtime-setup.exe"
    if (Test-Path $setupPath) {
        Remove-Item -Force $setupPath
    }

    # OutputBaseFilename 里不能带路径，所以输出文件名去掉 .exe 后传进去。
    $outBase = [System.IO.Path]::GetFileNameWithoutExtension($setupPath)

    & $isccExe `
        "/DMyAppVersion=$Version" `
        "/DOutDir=$distDir" `
        "/DOutBase=$outBase" `
        (Join-Path $PSScriptRoot 'QYPlayer.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup 编译失败，退出码 $LASTEXITCODE"
    }

    if (-not (Test-Path $setupPath)) {
        throw "编译结束但未找到安装包：$setupPath"
    }

    $setupMb = [math]::Round(((Get-Item $setupPath).Length / 1MB), 1)
    Write-Host "    $setupPath  ($setupMb MB)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 输出最终报告，包含体积与关键文件校验，避免"看起来成功其实缺文件"。
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==> 完成" -ForegroundColor Green
$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "    发布目录：$publishDir  ($sizeMb MB)"
Write-Host "    版本目录：$distDir"
Get-ChildItem $distDir | Sort-Object Name | ForEach-Object {
    Write-Host ("    - {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
}
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
