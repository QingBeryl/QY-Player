# 刷新 Windows 图标缓存
#
# 什么时候需要它：QY Player 是免安装的便携程序，发布时会在同一个路径上覆盖
# QYPlayer.exe。而资源管理器的图标缓存是「按路径」记的——路径没变，它就沿用
# 先前那条记录，于是文件里已经是新图标，画面上却还是旧的。
#
# 关键点：只重启资源管理器不够。缓存在 iconcache_*.db 里是持久化的，不删掉，
# 重启后会原样读回来。所以必须是「结束进程 → 删缓存 → 重启进程」这个顺序，
# 且删之前要先让资源管理器释放文件占用。
#
# 用法：在 PowerShell 里执行
#     powershell -ExecutionPolicy Bypass -File build\refresh-icon-cache.ps1
# 一般无需管理员权限；若提示拒绝访问，用管理员身份重开一个 PowerShell 再执行。
#
# 副作用：资源管理器会短暂消失（任务栏与桌面图标闪烁一下），随后自动恢复。
# 不影响任何已打开的其它程序。

$ErrorActionPreference = 'Stop'

$cacheDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
$legacyDb = Join-Path $env:LOCALAPPDATA 'IconCache.db'

Write-Host '==> 结束资源管理器，释放缓存文件占用' -ForegroundColor Cyan
Get-Process -Name explorer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$targets = @()
if (Test-Path $cacheDir) {
    $targets += Get-ChildItem -Path $cacheDir -Filter 'iconcache*' -Force -ErrorAction SilentlyContinue
}
if (Test-Path $legacyDb) {
    $targets += Get-Item $legacyDb -Force
}

$totalKb = 0
foreach ($t in $targets) { $totalKb += $t.Length }
Write-Host "==> 待清理 $($targets.Count) 个缓存文件，共 $([math]::Round($totalKb / 1KB)) KB"

# 文件句柄的释放不是瞬时的，单次删除常报「正由另一进程使用」，因此多试几轮。
$removed = 0
for ($round = 1; $round -le 5; $round++) {
    $pending = @($targets | Where-Object { Test-Path $_.FullName })
    if ($pending.Count -eq 0) { break }
    foreach ($f in $pending) {
        try {
            Remove-Item -Path $f.FullName -Force -ErrorAction Stop
            $removed++
        } catch {
            # 交给下一轮重试
        }
    }
    if (@($targets | Where-Object { Test-Path $_.FullName }).Count -gt 0) {
        Write-Host "    第 $round 轮后仍有文件被占用，等待重试…"
        Start-Sleep -Milliseconds 1500
    }
}

$left = @($targets | Where-Object { Test-Path $_.FullName })
Write-Host "==> 已删除 $removed 个，仍残留 $($left.Count) 个"
if ($left.Count -gt 0) {
    Write-Host '    （残留的多为 0 字节占位文件，影响不大）' -ForegroundColor DarkGray
    foreach ($f in $left) { Write-Host "    - $($f.Name)" -ForegroundColor DarkGray }
}

Write-Host '==> 重新启动资源管理器' -ForegroundColor Cyan
# 无论前面成败如何都要重启外壳，否则用户会没有任务栏和桌面。
Start-Process explorer.exe
Start-Sleep -Seconds 3

if (Get-Process -Name explorer -ErrorAction SilentlyContinue) {
    Write-Host '==> 完成。资源管理器已恢复运行，图标缓存已重建。' -ForegroundColor Green
    Write-Host '    若某个文件夹里仍显示旧图标，在那个文件夹里按 F5 刷新即可。'
} else {
    Write-Host '!! 资源管理器未能自动启动，请手动执行：Ctrl+Shift+Esc 打开任务管理器 → 文件 → 运行新任务 → 输入 explorer' -ForegroundColor Red
    exit 1
}
