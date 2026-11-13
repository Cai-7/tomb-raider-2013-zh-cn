<#
.SYNOPSIS
    《古墓丽影9》(2013) 简体中文 —— 一键安装社区完整汉化包

.DESCRIPTION
    下载 intergra/TR2013-Simplified-Chinese 的正式发布包，解压后用 Mod Manager
    的打包逻辑合并进 patch2.000.tiger。

    该汉化包重建了中文字库（Noto Sans SC），因此菜单、HUD、字幕不会出现方框。

    注意：Mod Manager 的 GUI 版本请使用 Nexus 上的 1.10.0。本脚本改用上游源码
    构建的 ModManager（1.11.0）命令行方式完成打包，实测安装后主菜单为完整
    简体中文、无方框。

.PARAMETER GamePath
    游戏根目录（含 TombRaider.exe）。不传则自动在 Steam 库中查找。

.PARAMETER Version
    汉化包版本标签，默认 v8.0。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install-community-mod.ps1
#>
param(
    [string]$GamePath = "",
    [string]$Version = "v8.0"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:PYTHONIOENCODING = "utf-8"

$Root = Split-Path -Parent $PSScriptRoot
$WorkDir = Join-Path $Root "work"
$Upstream = Join-Path $Root "tools\TrRebootModTools"
$ToolProj = Join-Path $Root "tools\TrTextTool\TrTextTool.csproj"
$ToolDll = Join-Path $Root "tools\TrTextTool\bin\Release\net8.0\TrTextTool.dll"
$FixPatch = Join-Path $Root "patches\0001-tr2013-localsbin-pointer-size.patch"
$ModUrl = "https://github.com/intergra/TR2013-Simplified-Chinese/releases/download/$Version/TR2013-Simplified-Chinese.zip"
$ModName = "TR2013-Simplified-Chinese"
$ModDir = Join-Path $WorkDir $ModName

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "错误：$Text" -ForegroundColor Red; exit 1 }

# ---------- 1. 环境 ----------
Write-Step "检查环境"
foreach ($cmd in @("dotnet", "git")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Fail "找不到 $cmd，请先安装：dotnet SDK 10 / Git"
    }
}
$sdk = (& dotnet --list-sdks) -join "`n"
if (-not ($sdk -split "`n" | Where-Object { $_ -match "^10\." })) {
    Fail ".NET SDK 10 未安装。上游代码使用 C# 14。已安装：`n$sdk"
}
Write-Host "dotnet OK"

# ---------- 2. 定位游戏 ----------
Write-Step "定位游戏目录"
function Find-SteamGame {
    $roots = @()
    $steamPath = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if ($steamPath) { $roots += $steamPath }
    foreach ($drive in @("C", "D", "E", "F", "G")) {
        if (Test-Path "${drive}:\SteamLibrary") { $roots += "${drive}:\SteamLibrary" }
    }
    foreach ($rootDir in $roots) {
        $vdf = Join-Path $rootDir "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches |
                ForEach-Object { $_.Matches } |
                ForEach-Object { $roots += ($_.Groups[1].Value -replace '\\\\', '\') }
        }
    }
    foreach ($rootDir in ($roots | Select-Object -Unique)) {
        $candidate = Join-Path $rootDir "steamapps\common\Tomb Raider"
        if (Test-Path (Join-Path $candidate "TombRaider.exe")) { return $candidate }
    }
    return $null
}

if (-not $GamePath) {
    $GamePath = Find-SteamGame
    if (-not $GamePath) { Fail "没有找到游戏目录，请用 -GamePath 指定" }
}
if (-not (Test-Path $GamePath)) { Fail "游戏目录不存在：$GamePath" }
$GamePath = (Resolve-Path $GamePath).Path

# 安装需要约 7 GB 空间：mod 档案 1.8 GB + 扁平化后的 patch2 1.8 GB + 解压后的汉化包 2 GB + 下载的 zip 1.1 GB
$driveName = (Split-Path -Qualifier $GamePath).TrimEnd(':')
$freeSpace = (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue).Free
if ($freeSpace -and $freeSpace -lt 8GB) {
    Fail ("磁盘空间不足：{0}: 需要约 8 GB 可用空间，当前只有 {1:N1} GB" -f $driveName, ($freeSpace / 1GB))
}
if (-not (Test-Path (Join-Path $GamePath "TombRaider.exe"))) {
    Fail "$GamePath 下没有 TombRaider.exe"
}
Write-Host "游戏目录：$GamePath"

if (Get-Process TombRaider -ErrorAction SilentlyContinue) { Fail "游戏正在运行，请先退出" }

# ---------- 3. 下载 ----------
Write-Step "下载汉化包 $Version（约 1.1 GB）"
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$zipPath = Join-Path $WorkDir "$ModName.zip"

if (-not (Test-Path $zipPath) -or (Get-Item $zipPath).Length -lt 100MB) {
    Write-Host "从 $ModUrl 下载 ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $wc = New-Object Net.WebClient
    try {
        $wc.DownloadFile($ModUrl, $zipPath)
    } catch {
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force -ErrorAction SilentlyContinue }
        Fail "下载失败：$($_.Exception.Message)`n可以手动下载后放到 $zipPath 再重试"
    }
}
Write-Host ("已下载 {0:N1} MB" -f ((Get-Item $zipPath).Length / 1MB))

# ---------- 4. 解压 ----------
Write-Step "解压"
if (Test-Path $ModDir) { Remove-Item $ModDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ModDir | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $ModDir)
}
catch {
    Fail "解压失败（文件可能下载不完整）：$($_.Exception.Message)`n请删除 $zipPath 后重试"
}
Write-Host "解压到 $ModDir"

# ---------- 5. 构建 ModManager ----------
Write-Step "准备 Mod Manager"
if (-not (Test-Path $Upstream)) {
    git clone --depth 1 https://github.com/arcusmaximus/TrRebootModTools.git $Upstream
    if ($LASTEXITCODE -ne 0) { Fail "克隆 TrRebootModTools 失败" }
}
$localsBin = Join-Path $Upstream "Shared\Cdc\Tr2013\Tr2013LocalsBin.cs"
if (-not (Select-String -Path $localsBin -Pattern "\(int\)stream\.Length" -Quiet)) {
    Push-Location $Upstream
    git apply $FixPatch
    Pop-Location
    Write-Host "已修复上游 locals.bin 写入 bug"
}

& dotnet build $ToolProj -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) { Fail "构建失败" }
Copy-Item (Join-Path $Upstream "Extractor\TR2013_PC_Release.list") `
          (Join-Path (Split-Path $ToolDll) "TR2013_PC_Release.list") -Force
Write-Host "构建完成"

# ---------- 6. 安装 ----------
Write-Step "安装到游戏"
& dotnet $ToolDll install $GamePath $ModDir
if ($LASTEXITCODE -ne 0) { Fail "安装失败" }

Write-Step "完成"
Write-Host "安装成功。启动游戏，界面与字幕即为简体中文。" -ForegroundColor Green

# 解压出来的目录约 2 GB，装完就没用了；zip 保留以便下次复用
Remove-Item $ModDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "注意："
Write-Host "  · 游戏语言请设为 Chinese / 中文（游戏内 语言设置，或 Steam 属性 -> 语言）"
Write-Host "  · 语音仍是英文，官方无中文配音"
Write-Host "  · 卸载：dotnet `"$ToolDll`" uninstall `"$GamePath`"" -ForegroundColor DarkGray
Write-Host "  · Steam 验证文件完整性或重装游戏后，补丁会失效，重新运行本脚本即可" -ForegroundColor DarkGray
Write-Host "  · 已删除解压目录以省空间；下载的 zip 保留在 $zipPath" -ForegroundColor DarkGray
