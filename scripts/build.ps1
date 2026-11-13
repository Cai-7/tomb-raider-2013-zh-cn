<#
.SYNOPSIS
    《古墓丽影9》(Tomb Raider 2013) 简体中文补丁 —— 一键构建并安装

.DESCRIPTION
    依次完成：环境检查 -> 拉取上游工具 -> 构建文本工具 -> 提取官方繁体文本
    -> 繁转简 -> 安装 mod 并合并进 patch2.000.tiger

.PARAMETER GamePath
    游戏根目录（含 TombRaider.exe）。不传则自动在 Steam 库中查找。

.PARAMETER SkipInstall
    只生成 mod 文件，不写入游戏目录。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -GamePath "D:\Games\Tomb Raider"
#>
param(
    [string]$GamePath = "",
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:PYTHONIOENCODING = "utf-8"
$Root = Split-Path -Parent $PSScriptRoot
$WorkDir = Join-Path $Root "work"
$ModDir = Join-Path $Root "mod\zh-Hans"
$Upstream = Join-Path $Root "tools\TrRebootModTools"
$ToolProj = Join-Path $Root "tools\TrTextTool\TrTextTool.csproj"
$ToolDll = Join-Path $Root "tools\TrTextTool\bin\Release\net8.0\TrTextTool.dll"
$FixPatch = Join-Path $Root "patches\0001-tr2013-localsbin-pointer-size.patch"

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "错误：$Text" -ForegroundColor Red; exit 1 }

# ---------- 1. 环境检查 ----------
Write-Step "检查环境"

foreach ($cmd in @("dotnet", "python", "git")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Fail "找不到 $cmd，请先安装：dotnet SDK 10 / Python 3 / Git"
    }
}

$sdk = (& dotnet --list-sdks) -join "`n"
if (-not ($sdk -split "`n" | Where-Object { $_ -match "^10\." })) {
    Fail ".NET SDK 10 未安装（上游代码使用 C# 14）。已安装：`n$sdk"
}
Write-Host "dotnet OK"

# ---------- 2. 定位游戏目录 ----------
Write-Step "定位游戏目录"

function Find-SteamGame {
    $roots = @()
    $steamPath = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if ($steamPath) { $roots += $steamPath }
    foreach ($drive in @("C", "D", "E", "F", "G")) {
        $candidate = "${drive}:\SteamLibrary"
        if (Test-Path $candidate) { $roots += $candidate }
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
    if (-not $GamePath) { Fail "没有找到游戏目录，请用 -GamePath 指定（包含 TombRaider.exe 的文件夹）" }
}
$GamePath = (Resolve-Path $GamePath).Path
if (-not (Test-Path (Join-Path $GamePath "TombRaider.exe"))) {
    Fail "$GamePath 下没有 TombRaider.exe，确认路径是否正确"
}
Write-Host "游戏目录：$GamePath"

if (Get-Process TombRaider -ErrorAction SilentlyContinue) {
    Fail "游戏正在运行，请先退出游戏"
}

# ---------- 3. Python 依赖 ----------
Write-Step "安装 Python 依赖"
python -c "import opencc" 2>$null
if ($LASTEXITCODE -ne 0) {
    python -m pip install --quiet opencc-python-reimplemented
    if ($LASTEXITCODE -ne 0) { Fail "opencc 安装失败，请手动执行：pip install opencc-python-reimplemented" }
}
Write-Host "opencc OK"

# ---------- 4. 拉取上游工具 ----------
Write-Step "准备上游工具 TrRebootModTools"

if (-not (Test-Path $Upstream)) {
    git clone --depth 1 https://github.com/arcusmaximus/TrRebootModTools.git $Upstream
    if ($LASTEXITCODE -ne 0) { Fail "克隆 TrRebootModTools 失败，请检查网络" }
}

$localsBin = Join-Path $Upstream "Shared\Cdc\Tr2013\Tr2013LocalsBin.cs"
if (-not (Select-String -Path $localsBin -Pattern "\(int\)stream\.Length" -Quiet)) {
    Push-Location $Upstream
    git apply $FixPatch
    if ($LASTEXITCODE -ne 0) { Pop-Location; Fail "应用修复补丁失败" }
    Pop-Location
    Write-Host "已修复上游 locals.bin 写入 bug"
} else {
    Write-Host "上游修复已存在，跳过"
}

# ---------- 5. 构建文本工具 ----------
Write-Step "构建 TrTextTool"
& dotnet build $ToolProj -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) { Fail "构建失败" }

# 哈希对照表需要和 exe 放在同一目录
$hashList = Join-Path $Upstream "Extractor\TR2013_PC_Release.list"
Copy-Item $hashList (Join-Path (Split-Path $ToolDll) "TR2013_PC_Release.list") -Force
Write-Host "构建完成：$ToolDll"

# ---------- 6. 提取官方繁体文本 ----------
Write-Step "提取官方繁体文本"

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
& dotnet $ToolDll dump $GamePath $WorkDir zhhant
if ($LASTEXITCODE -ne 0) { Fail "提取失败" }

$zhHantJson = Join-Path $WorkDir "zhhant\pc-w\local\locals.json"
if (-not (Test-Path $zhHantJson)) { Fail "没有提取到繁体文本，可能游戏版本不匹配" }

# ---------- 7. 繁转简 ----------
Write-Step "繁转简"
if (Test-Path $ModDir) { Remove-Item $ModDir -Recurse -Force }
$modJson = Join-Path $ModDir "pc-w\local\locals.bin\zhhant.json"
python (Join-Path $Root "scripts\build_patch.py") --input $zhHantJson --output $modJson
if ($LASTEXITCODE -ne 0) { Fail "繁转简失败（见上方警告）" }

# ---------- 8. 安装 ----------
if ($SkipInstall) {
    Write-Step "已跳过安装"
    Write-Host "mod 文件在：$ModDir"
    Write-Host "手动安装：dotnet `"$ToolDll`" install `"$GamePath`" `"$ModDir`""
    exit 0
}

Write-Step "安装到游戏"
& dotnet $ToolDll install $GamePath $ModDir
if ($LASTEXITCODE -ne 0) { Fail "安装失败" }

Write-Step "完成"
Write-Host "启动游戏即可看到简体中文。" -ForegroundColor Green
Write-Host "卸载：dotnet `"$ToolDll`" uninstall `"$GamePath`"" -ForegroundColor DarkGray
