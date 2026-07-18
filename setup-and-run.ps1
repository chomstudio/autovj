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
$sampleArchiveName = "autovj-samples-v0.8.0.zip"
$sampleDownloadUrl = "https://github.com/chomstudio/autovj/releases/download/v0.8.0/$sampleArchiveName"
$sampleArchiveSha256 = "223F34CDF9680452B8A6E4B93FD11ABC5E5966901F0124B33C935F9932AEA4F8"
$sampleInstallMarker = Join-Path $toolsRoot "samples-v0.8.0.installed"

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

    # ネイティブ出力をPowerShellのパイプで再エンコードせず、コンソールへ直接表示します。
    & $Executable @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Executable の実行に失敗しました。終了コード: $exitCode"
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

# GitHub Releaseから検証済みサンプルを取得し、既存ファイルを保護して配置します。
function Install-SampleData {
    $requiredSampleFiles = @(
        "main-videos\video-e-part.mp4",
        "main-videos\video-k-part.mp4",
        "main-videos\video-m-part.mp4",
        "main-videos\video-o-part.mp4",
        "main-videos\video-s-part.mp4",
        "material-videos\common_movie.mp4",
        "material-videos\glitch1.mp4",
        "material-videos\glitch2.mp4",
        "material-videos\glitch3.mp4"
    )
    if (Test-Path -LiteralPath $sampleInstallMarker -PathType Leaf) {
        $installedHash = (Get-Content -LiteralPath $sampleInstallMarker -Raw).Trim()
        $allSamplesExist = $null -eq (
            $requiredSampleFiles |
                Where-Object { -not (Test-Path -LiteralPath (Join-Path $projectRoot $_) -PathType Leaf) } |
                Select-Object -First 1
        )
        if ($installedHash -eq $sampleArchiveSha256 -and $allSamplesExist) {
            Write-Host "サンプル動画は導入済みです。"
            return
        }
    }

    Write-Step "サンプル動画を準備しています"

    $archivePath = Join-Path $downloadsRoot $sampleArchiveName
    $extractRoot = Join-Path $toolsRoot "samples-extract"
    $archiveIsValid = (Test-Path -LiteralPath $archivePath -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $sampleArchiveSha256)
    if ($archiveIsValid) {
        Write-Host "ダウンロード済みのサンプル書庫を使用します。"
    }
    else {
        Invoke-WebRequest -UseBasicParsing -Uri $sampleDownloadUrl -OutFile $archivePath
    }

    $downloadedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($downloadedHash -ne $sampleArchiveSha256) {
        Remove-Item -LiteralPath $archivePath -Force
        throw "サンプル動画のSHA-256が一致しません。ダウンロードをやり直してください。"
    }

    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force

    foreach ($relativePath in $requiredSampleFiles) {
        $extractedPath = Join-Path $extractRoot $relativePath
        if (-not (Test-Path -LiteralPath $extractedPath -PathType Leaf)) {
            throw "サンプル動画の書庫内に必要なファイルがありません: $relativePath"
        }
    }

    $copiedCount = 0
    $unchangedCount = 0
    $preservedCount = 0
    foreach ($sourceFile in Get-ChildItem -LiteralPath $extractRoot -File -Recurse) {
        $relativePath = $sourceFile.FullName.Substring($extractRoot.Length).TrimStart([char[]]"\/")
        $destinationPath = Join-Path $projectRoot $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null

        if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
            if ($sourceHash -eq $destinationHash) {
                $unchangedCount++
            }
            else {
                Write-Warning "既存ファイルを上書きせず保持しました: $relativePath"
                $preservedCount++
            }
            continue
        }

        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath
        $copiedCount++
    }

    Remove-Item -LiteralPath $extractRoot -Recurse -Force
    Remove-Item -LiteralPath $archivePath -Force
    Set-Content -LiteralPath $sampleInstallMarker -Value $sampleArchiveSha256 -Encoding Ascii
    Write-Host "サンプル動画を配置しました: 新規=$copiedCount / 配置済み=$unchangedCount / 既存優先=$preservedCount"
}

# サンプル導入後にカタログを解析し、成功しなければAutoVJを起動させません。
function Invoke-CatalogScan {
    param([Parameter(Mandatory)][string]$DotNetPath)

    $mainVideoRoot = Join-Path $projectRoot "main-videos"
    $hasMainVideo = $null -ne (
        Get-ChildItem -LiteralPath $mainVideoRoot -Filter "*.mp4" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
    )
    if (-not $hasMainVideo) {
        throw "main-videosに解析対象のMP4がありません。"
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
    Install-SampleData
    Invoke-CatalogScan -DotNetPath $dotNetPath

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
