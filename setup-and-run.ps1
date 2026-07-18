[CmdletBinding()]
param(
    [switch]$SkipRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$toolsRoot = Join-Path $projectRoot ".tools"
$downloadsRoot = Join-Path $toolsRoot "downloads"
$localDotNetRoot = Join-Path $toolsRoot "dotnet"
$ffmpegRoot = Join-Path $toolsRoot "ffmpeg"
$ffmpegBin = Join-Path $ffmpegRoot "bin"
$buildRoot = Join-Path $projectRoot ".build"
$mainBuildRoot = Join-Path $buildRoot "AutoVJ"
$catalogBuildRoot = Join-Path $buildRoot "Catalog"
$nugetPackagesRoot = Join-Path $toolsRoot "nuget-packages"

# 処理の区切りを見やすい形式で表示します。
function Write-Step {
    param([Parameter(Mandatory)][string]$Message)

    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

# 外部コマンドを実行し、終了コードが0以外なら処理を中断します。
function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable の実行に失敗しました。終了コード: $LASTEXITCODE"
    }
}

# 指定したdotnetに.NET 10 SDKが含まれているか確認します。
function Test-DotNet10Sdk {
    param([Parameter(Mandatory)][string]$DotNetPath)

    if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
        return $false
    }

    try {
        $sdkList = & $DotNetPath --list-sdks 2>$null
        return $null -ne ($sdkList | Where-Object { $_ -match "^10\." } | Select-Object -First 1)
    }
    catch {
        return $false
    }
}

# ローカルまたはシステムから利用可能な.NET 10 SDKを探します。
function Find-DotNet10Sdk {
    $localDotNet = Join-Path $localDotNetRoot "dotnet.exe"
    if (Test-DotNet10Sdk -DotNetPath $localDotNet) {
        return $localDotNet
    }

    $systemDotNet = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $systemDotNet -and (Test-DotNet10Sdk -DotNetPath $systemDotNet.Source)) {
        return $systemDotNet.Source
    }

    return $null
}

# Microsoft公式スクリプトを使い、管理者権限なしで.NET 10 SDKを導入します。
function Install-DotNet10Sdk {
    Write-Step ".NET 10 SDKをダウンロードしています"

    New-Item -ItemType Directory -Force -Path $downloadsRoot, $localDotNetRoot | Out-Null
    $installerPath = Join-Path $downloadsRoot "dotnet-install.ps1"
    Invoke-WebRequest `
        -UseBasicParsing `
        -Uri "https://dot.net/v1/dotnet-install.ps1" `
        -OutFile $installerPath

    $powerShellPath = (Get-Process -Id $PID).Path
    Invoke-CheckedCommand -Executable $powerShellPath -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $installerPath,
        "-Channel", "10.0",
        "-Quality", "GA",
        "-InstallDir", $localDotNetRoot,
        "-NoPath"
    )

    $localDotNet = Join-Path $localDotNetRoot "dotnet.exe"
    if (-not (Test-DotNet10Sdk -DotNetPath $localDotNet)) {
        throw "導入した.NET 10 SDKを確認できませんでした。"
    }
    return $localDotNet
}

# BtbN配布のWindows向けLGPL版FFmpegをプロジェクト内へ導入します。
function Install-Ffmpeg {
    $ffmpegPath = Join-Path $ffmpegBin "ffmpeg.exe"
    $ffprobePath = Join-Path $ffmpegBin "ffprobe.exe"
    if ((Test-Path -LiteralPath $ffmpegPath -PathType Leaf) -and
        (Test-Path -LiteralPath $ffprobePath -PathType Leaf)) {
        Write-Host "FFmpegは導入済みです。"
        return
    }

    Write-Step "FFmpegのLGPL版をダウンロードしています"

    New-Item -ItemType Directory -Force -Path $downloadsRoot | Out-Null
    $archivePath = Join-Path $downloadsRoot "ffmpeg-win64-lgpl-shared.zip"
    $extractRoot = Join-Path $toolsRoot "ffmpeg-extract"
    $downloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl-shared.zip"

    Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $archivePath

    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $ffmpegRoot) {
        Remove-Item -LiteralPath $ffmpegRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $extractRoot, $ffmpegRoot | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force

    $extractedFfmpeg = Get-ChildItem -LiteralPath $extractRoot -Filter "ffmpeg.exe" -File -Recurse |
        Select-Object -First 1
    if ($null -eq $extractedFfmpeg -or $extractedFfmpeg.Directory.Name -ne "bin") {
        throw "ダウンロードした書庫内にffmpeg.exeを確認できませんでした。"
    }

    $packageRoot = $extractedFfmpeg.Directory.Parent.FullName
    Get-ChildItem -LiteralPath $packageRoot -Force |
        Copy-Item -Destination $ffmpegRoot -Recurse -Force

    Remove-Item -LiteralPath $extractRoot -Recurse -Force
    Remove-Item -LiteralPath $archivePath -Force

    if (-not ((Test-Path -LiteralPath $ffmpegPath -PathType Leaf) -and
        (Test-Path -LiteralPath $ffprobePath -PathType Leaf))) {
        throw "FFmpegの展開後にffmpeg.exeまたはffprobe.exeを確認できませんでした。"
    }
}

# プロジェクトの実行に必要な環境変数を現在の処理内だけ設定します。
function Set-ProjectEnvironment {
    param([Parameter(Mandatory)][string]$DotNetPath)

    $dotNetRoot = Split-Path -Parent $DotNetPath
    $env:DOTNET_ROOT = $dotNetRoot
    $env:PATH = "$dotNetRoot;$ffmpegBin;$env:PATH"
    $env:NUGET_PACKAGES = $nugetPackagesRoot
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"
}

# NuGetパッケージを復元し、2つの実行プログラムをRelease構成で出力します。
function Build-AutoVJ {
    param([Parameter(Mandatory)][string]$DotNetPath)

    Write-Step "必要なライブラリを復元しています"
    Invoke-CheckedCommand -Executable $DotNetPath -Arguments @(
        "restore", (Join-Path $projectRoot "AutoVJ.csproj"),
        "--source", "https://api.nuget.org/v3/index.json"
    )
    Invoke-CheckedCommand -Executable $DotNetPath -Arguments @(
        "restore", (Join-Path $projectRoot "Catalog\AutoVJ.Catalog.csproj"),
        "--source", "https://api.nuget.org/v3/index.json"
    )

    Write-Step "AutoVJをビルドしています"
    if (Test-Path -LiteralPath $buildRoot) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $mainBuildRoot, $catalogBuildRoot | Out-Null

    Invoke-CheckedCommand -Executable $DotNetPath -Arguments @(
        "publish", (Join-Path $projectRoot "AutoVJ.csproj"),
        "--configuration", "Release", "--no-restore", "--output", $mainBuildRoot
    )
    Invoke-CheckedCommand -Executable $DotNetPath -Arguments @(
        "publish", (Join-Path $projectRoot "Catalog\AutoVJ.Catalog.csproj"),
        "--configuration", "Release", "--no-restore", "--output", $catalogBuildRoot
    )
}

# 初回利用に必要な素材・データ用フォルダを作成します。
function Initialize-ProjectDirectories {
    foreach ($directoryName in @("main-videos", "material-videos", "data")) {
        New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot $directoryName) | Out-Null
    }
}

# 個人設定がまだない場合だけ、公開用の初期設定をコピーします。
function Initialize-UserConfig {
    $configPath = Join-Path $projectRoot "config.yaml"
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        Write-Host "既存のconfig.yamlを使用します。"
        return
    }

    $defaultConfigPath = Join-Path $projectRoot "config-default.yaml"
    if (-not (Test-Path -LiteralPath $defaultConfigPath -PathType Leaf)) {
        throw "公開用設定ファイルが見つかりません: $defaultConfigPath"
    }

    Copy-Item -LiteralPath $defaultConfigPath -Destination $configPath
    Write-Host "config-default.yamlからconfig.yamlを作成しました。"
}

# メイン動画が配置済みなら、初回起動前にカタログを差分解析します。
function Invoke-InitialCatalogScan {
    param([Parameter(Mandatory)][string]$DotNetPath)

    $mainVideoRoot = Join-Path $projectRoot "main-videos"
    $hasMainVideo = $null -ne (
        Get-ChildItem -LiteralPath $mainVideoRoot -Filter "*.mp4" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
    )
    if (-not $hasMainVideo) {
        Write-Host "main-videosにMP4がないため、動画解析は省略します。"
        return
    }

    Write-Step "main-videosの動画を解析しています"
    Invoke-CheckedCommand -Executable $DotNetPath -Arguments @(
        (Join-Path $catalogBuildRoot "AutoVJ.Catalog.dll"), "scan"
    )
}

try {
    Set-Location -LiteralPath $projectRoot
    if ($env:OS -ne "Windows_NT" -or -not [Environment]::Is64BitOperatingSystem) {
        throw "このスクリプトは64bit版Windows専用です。"
    }

    Write-Step "AutoVJのセットアップを開始します"
    Initialize-UserConfig
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Force -Path $toolsRoot, $downloadsRoot, $nugetPackagesRoot | Out-Null

    $dotNetPath = Find-DotNet10Sdk
    if ($null -eq $dotNetPath) {
        $dotNetPath = Install-DotNet10Sdk
    }
    else {
        Write-Host "利用可能な.NET 10 SDKを確認しました: $dotNetPath"
    }

    Install-Ffmpeg
    Set-ProjectEnvironment -DotNetPath $dotNetPath
    Initialize-ProjectDirectories
    Build-AutoVJ -DotNetPath $dotNetPath
    Invoke-InitialCatalogScan -DotNetPath $dotNetPath

    Write-Step "セットアップが完了しました"
    Write-Host "通常起動: run-autovj.cmd"
    Write-Host "動画解析: scan-videos.cmd"

    if (-not $SkipRun) {
        Write-Step "AutoVJを起動します"
        Invoke-CheckedCommand -Executable $dotNetPath -Arguments @(
            (Join-Path $mainBuildRoot "AutoVJ.dll")
        )
    }
}
catch {
    Write-Host ""
    Write-Host "セットアップに失敗しました。" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
