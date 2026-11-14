# 下载 Windows 版 ffmpeg（gyan.dev release essentials，含 h264_mf / hevc_mf），把 ffmpeg.exe 放到 -Dest 目录。
# 用法：powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1 -Dest "<游戏目录>\BepInEx\plugins\GalleryRecorder\ffmpeg"
param(
    [Parameter(Mandatory = $true)][string]$Dest,
    [string]$Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
)
$ErrorActionPreference = "Stop"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp "ffmpeg.zip"
    Write-Host "下载 $Url ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $exe = Get-ChildItem -Path $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if (-not $exe) { throw "压缩包里没找到 ffmpeg.exe" }
    New-Item -ItemType Directory -Force $Dest | Out-Null
    Copy-Item $exe.FullName (Join-Path $Dest "ffmpeg.exe") -Force
    $lic = Get-ChildItem -Path $exe.Directory.Parent.FullName -Filter "LICENSE*" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($lic) { Copy-Item $lic.FullName (Join-Path $Dest "FFMPEG-LICENSE.txt") -Force }
    $enc = & (Join-Path $Dest "ffmpeg.exe") -hide_banner -encoders 2>$null | Select-String "_mf"
    Write-Host "已安装到 $Dest"
    if ($enc) { $enc | ForEach-Object { Write-Host "  $_" } } else { Write-Warning "这个 ffmpeg 没有 MediaFoundation 编码器（h264_mf/hevc_mf），需要改录制和压缩参数。" }
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
