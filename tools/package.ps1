# 编译并打包：每个 zip 都可以直接解压到游戏根目录（里面是 BepInEx\plugins\GalleryRecorder\...）。
# 用法：powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }
    $dist = Join-Path $root "dist"
    Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dist | Out-Null

    $items = @(@{ Name = "Core"; Dll = "src\GalleryRecorder.Core\bin\Release\GalleryRecorder.Core.dll" })
    Get-ChildItem (Join-Path $root "adapters") -Directory | ForEach-Object {
        $dll = Get-ChildItem (Join-Path $_.FullName "bin\Release") -Filter "GalleryRecorder.*.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne "GalleryRecorder.Core.dll" } | Select-Object -First 1
        if ($dll) { $items += @{ Name = $_.Name; Dll = $dll.FullName } }
    }
    foreach ($it in $items) {
        $stage = Join-Path $dist ("_stage_" + $it.Name)
        $plug = Join-Path $stage "BepInEx\plugins\GalleryRecorder"
        New-Item -ItemType Directory -Force $plug | Out-Null
        Copy-Item (Join-Path $root $it.Dll) $plug -ErrorAction SilentlyContinue
        if (-not (Test-Path (Join-Path $plug (Split-Path $it.Dll -Leaf)))) { Copy-Item $it.Dll $plug }
        $zip = Join-Path $dist ("GalleryRecorder-" + $it.Name + ".zip")
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
        Remove-Item -Recurse -Force $stage
        Write-Host "  $zip"
    }

    # RPG Maker MV：每个扩展包一个 zip，内含 core、该扩展包和安装脚本（目录结构同仓库）
    $mv = Join-Path $root "rpgmaker-mv"
    Get-ChildItem (Join-Path $mv "adapters") -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $stage = Join-Path $dist ("_stage_mv_" + $_.Name)
        $dst = Join-Path $stage "rpgmaker-mv"
        New-Item -ItemType Directory -Force (Join-Path $dst "adapters") | Out-Null
        Copy-Item -Recurse (Join-Path $mv "core") $dst
        Copy-Item -Recurse (Join-Path $mv "tools") $dst
        Copy-Item -Recurse $_.FullName (Join-Path $dst "adapters")
        Copy-Item (Join-Path $mv "README.md") $dst
        $zip = Join-Path $dist ("GalleryRecorder-MV-" + $_.Name + ".zip")
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
        Remove-Item -Recurse -Force $stage
        Write-Host "  $zip"
    }
}
finally { Pop-Location }
