<#
.SYNOPSIS
    《古墓丽影9》(2013) 简体中文 —— 从源码构建并运行一键安装器

.DESCRIPTION
    构建 installer\Tr2013CnInstaller 并调用其命令行模式安装汉化。

    重要：安装器固定在 Mod Manager 1.10.0（上游提交 5309f18），这是汉化作者
    实测验证的版本。上游 1.11.0 会改变资源集合（.drm）的重写结果，实测在
    滑索等场景会卡死，因此这里固定回 1.10.0 并打上 locals.bin 写入修复。

.PARAMETER GamePath
    游戏根目录（含 TombRaider.exe）。不传则自动在 Steam 库中查找。

.PARAMETER Zip
    使用本地汉化包（可选）。不传则由安装器从作者官方 Release 下载。

.PARAMETER Uninstall
    改为执行卸载。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install-community-mod.ps1
#>
param(
    [string]$GamePath = "",
    [string]$Zip = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$Root = Split-Path -Parent $PSScriptRoot
$ToolsDir = Join-Path $Root "tools\TrRebootModTools-1.10"
$InstallerProj = Join-Path $Root "installer\Tr2013CnInstaller\Tr2013CnInstaller.csproj"
$InstallerExe = Join-Path $Root "installer\Tr2013CnInstaller\bin\Release\net8.0-windows\TR2013CnInstaller.exe"
$FixPatch = Join-Path $Root "patches\0001-tr2013-localsbin-pointer-size.patch"
$PinnedCommit = "5309f18"

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "错误：$Text" -ForegroundColor Red; exit 1 }

# ---------- 1. 环境 ----------
Write-Step "检查环境"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "找不到 dotnet，请先安装 .NET SDK 10" }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail "找不到 git" }

$sdk = (& dotnet --list-sdks) -join "`n"
if (-not ($sdk -split "`n" | Where-Object { $_ -match "^10\." })) {
    Fail ".NET SDK 10 未安装（1.10.0 源码使用 C# 14）。已安装：`n$sdk"
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
if (-not (Test-Path (Join-Path $GamePath "TombRaider.exe"))) { Fail "$GamePath 下没有 TombRaider.exe" }
Write-Host "游戏目录：$GamePath"

# ---------- 3. 准备 1.10.0 工具 ----------
Write-Step "准备 Mod Manager 1.10.0（作者验证版本）"

if (-not (Test-Path $ToolsDir)) {
    git clone https://github.com/arcusmaximus/TrRebootModTools.git $ToolsDir
    if ($LASTEXITCODE -ne 0) { Fail "克隆 TrRebootModTools 失败" }
}
Push-Location $ToolsDir
$current = (git rev-parse --short HEAD).Trim()
if (-not $current.StartsWith($PinnedCommit)) {
    Write-Host "切换到固定提交 $PinnedCommit ..."
    git fetch --depth 1 origin $PinnedCommit
    if ($LASTEXITCODE -ne 0) { git fetch origin $PinnedCommit }
    git checkout -q $PinnedCommit
    if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "切换提交失败" }
}
$localsBin = Join-Path $ToolsDir "Shared\Cdc\Tr2013\Tr2013LocalsBin.cs"
if (-not (Select-String -Path $localsBin -Pattern "\(int\)stream\.Length" -Quiet)) {
    git apply $FixPatch
    if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "应用 locals.bin 写入修复失败" }
    Write-Host "已应用 locals.bin 写入修复"
}
Pop-Location
Write-Host "工具版本：1.10.0（提交 $PinnedCommit）"

# ---------- 4. 构建安装器 ----------
Write-Step "构建安装器"
& dotnet build $InstallerProj -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) { Fail "构建失败" }
if (-not (Test-Path $InstallerExe)) { Fail "没有找到构建产物 $InstallerExe" }

# ---------- 5. 运行 ----------
Write-Step $(if ($Uninstall) { "卸载" } else { "安装" })
# 注意：$InstallerExe 是 GUI 程序，PowerShell 的 & 调用不会等待它结束，
# 必须用 Start-Process -Wait，否则脚本会在安装还没跑完时就报告成功。
# 路径可能含空格（"Tomb Raider"），Start-Process 不会自动加引号，必须自己加
function Quote([string]$v) { '"' + $v + '"' }

if ($Uninstall) {
    $argList = "--uninstall $(Quote $GamePath)"
} elseif ($Zip) {
    $argList = "--install $(Quote $GamePath) --zip $(Quote $Zip)"
} else {
    $argList = "--install $(Quote $GamePath)"
}

$proc = Start-Process -FilePath $InstallerExe -ArgumentList $argList -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) { Fail "操作失败（退出码 $($proc.ExitCode)）" }

Write-Step "完成"
Write-Host "启动游戏，在「语言」设置里选 Chinese / 中文。" -ForegroundColor Green
Write-Host "语音仍是英文，官方无中文配音。" -ForegroundColor DarkGray
