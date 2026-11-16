# 把 RPG Maker MV 版核心 + 扩展包装进游戏。
# 用法：
#   powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "<游戏目录>" -Adapter SAO2 [-Ffmpeg "<ffmpeg.exe 路径>"]
#   powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "<游戏目录>" -Uninstall
# 做的事：
#   1. 复制 GalleryRecorder_Core.js 和扩展包 js 到 www\js\plugins\
#   2. 在 www\js\plugins.js 末尾登记这两个插件（第一次会备份为 plugins.js.gr-backup）
#   3. 在根目录 package.json 加 chromium-args，让窗口被挡住 / 在别的虚拟桌面时游戏照常运行（备份为 package.json.gr-backup）
#   4. 可选：复制 ffmpeg.exe 到 <游戏目录>\GalleryRecorder\ffmpeg\
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$Adapter,
    [string]$Ffmpeg,
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $PSScriptRoot          # rpgmaker-mv\
$utf8 = New-Object System.Text.UTF8Encoding($false)

$www = Join-Path $GameDir "www"
if (-not (Test-Path (Join-Path $www "js\plugins.js"))) { throw "不是 RPG Maker MV 游戏目录（找不到 www\js\plugins.js）：$GameDir" }
$pluginsJs = Join-Path $www "js\plugins.js"
$pluginDir = Join-Path $www "js\plugins"

function Read-Text($p) { [IO.File]::ReadAllText($p, $utf8) }
function Write-Text($p, $s) { [IO.File]::WriteAllText($p, $s, $utf8) }

# plugins.js 里已有的 GalleryRecorder_* 登记行全部去掉
$text = Read-Text $pluginsJs
$text = [regex]::Replace($text, '(?m)^,?\s*\{"name":"GalleryRecorder_[^"]*".*\}\s*,?\s*\r?\n', '')
$text = [regex]::Replace($text, ',(\s*)\];\s*$', '$1];')

if ($Uninstall) {
    Write-Text $pluginsJs $text
    Get-ChildItem $pluginDir -Filter "GalleryRecorder_*.js" | Remove-Item
    Write-Host "已卸载（package.json 的 chromium-args 和 GalleryRecorder 目录保留）"
    return
}
if (-not $Adapter) { throw "需要 -Adapter（adapters 下的目录名）" }
$adapterDir = Join-Path $here "adapters\$Adapter"
$adapterJs = Get-ChildItem $adapterDir -Filter "GalleryRecorder_*.js" | Select-Object -First 1
if (-not $adapterJs) { throw "找不到扩展包：$adapterDir" }

if (-not (Test-Path "$pluginsJs.gr-backup")) { Copy-Item $pluginsJs "$pluginsJs.gr-backup" }
Get-ChildItem $pluginDir -Filter "GalleryRecorder_*.js" | Remove-Item
Copy-Item (Join-Path $here "core\GalleryRecorder_Core.js") $pluginDir
Copy-Item $adapterJs.FullName $pluginDir

$names = @("GalleryRecorder_Core", [IO.Path]::GetFileNameWithoutExtension($adapterJs.Name))
$lines = ($names | ForEach-Object { '{"name":"' + $_ + '","status":true,"description":"","parameters":{}}' }) -join ",`n"
$idx = $text.LastIndexOf("];")
if ($idx -lt 0) { throw "plugins.js 格式不认识" }
$before = $text.Substring(0, $idx).TrimEnd()
$sep = if ($before.EndsWith("[")) { "`n" } else { ",`n" }
$text = $before + $sep + $lines + "`n" + $text.Substring($idx)
Write-Text $pluginsJs $text
Write-Host "已登记插件：$($names -join ', ')"

# chromium-args：不让 Chromium 在窗口被遮挡 / 后台时降帧或暂停
$pkg = Join-Path $GameDir "package.json"
if (Test-Path $pkg) {
    $p = Read-Text $pkg
    if ($p -notmatch '"chromium-args"') {
        if (-not (Test-Path "$pkg.gr-backup")) { Copy-Item $pkg "$pkg.gr-backup" }
        $chromeArgs = '--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion'
        $i = $p.IndexOf("{")
        $p = $p.Substring(0, $i + 1) + "`n  `"chromium-args`": `"$chromeArgs`"," + $p.Substring($i + 1)
        Write-Text $pkg $p
        Write-Host "已在 package.json 加入 chromium-args"
    }
}

$ffDir = Join-Path $GameDir "GalleryRecorder\ffmpeg"
New-Item -ItemType Directory -Force $ffDir | Out-Null
if ($Ffmpeg) {
    Copy-Item $Ffmpeg $ffDir -Force
    Get-ChildItem (Split-Path -Parent $Ffmpeg) -Filter "*.dll" | Copy-Item -Destination $ffDir -Force
    Write-Host "已复制 ffmpeg"
}
if (-not (Test-Path (Join-Path $ffDir "ffmpeg.exe"))) {
    Write-Warning "还没有 ffmpeg：请把带 h264_mf / hevc_mf 的 ffmpeg.exe 放到 $ffDir（可用 tools\get-ffmpeg.ps1 下载）"
}
Write-Host "完成。启动游戏，读档进入地图后按 F8。"
