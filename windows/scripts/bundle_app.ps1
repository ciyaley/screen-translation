param(
    [switch]$StageUpdate
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot

$DotnetCmd = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
$DotnetBin = if ($DotnetCmd) { $DotnetCmd.Source } elseif (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { $null }
if (-not $DotnetBin) {
    Write-Error ".NET SDK (8.0 または 9.0 以上) が見つかりません。"
    exit 1
}

$SdkList = (& $DotnetBin --list-sdks 2>$null | Out-String)
$MajorVer = if ($SdkList -match '(?m)^9\.') { 9 } elseif ($SdkList -match '(?m)^8\.') { 8 } else { 9 }
$CoreTfm = "net$MajorVer.0"
$AppTfm = "net$MajorVer.0-windows10.0.19041.0"

$OutDir = if ($StageUpdate) { Join-Path $RootDir "build.next" } else { Join-Path $RootDir "dist" }

Push-Location $RootDir
try {
    Write-Host "[1/3] Running ScreenTranslate.Verify ($CoreTfm)..."
    & $DotnetBin run --project "src\ScreenTranslate.Verify\ScreenTranslate.Verify.csproj" -c Release "-p:WinCoreTfm=$CoreTfm"
    if ($LASTEXITCODE -ne 0) { throw "Verification failed ($LASTEXITCODE)" }

    Write-Host "[2/3] Publishing single-file Windows x64 executable ($AppTfm)..."
    & $DotnetBin publish "src\ScreenTranslate.App\ScreenTranslate.App.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:WinCoreTfm=$CoreTfm" `
        "-p:WinAppTfm=$AppTfm" `
        -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)" }

    $BuiltExe = Join-Path $OutDir "ScreenTranslate.exe"
    Write-Host "[3/3] Running UI self-test on $BuiltExe..."
    $TestDir = Join-Path $env:TEMP "st-win-selftest-$PID"
    New-Item -ItemType Directory -Force -Path $TestDir | Out-Null
    try {
        $prev = $env:SCREEN_TRANSLATE_RUNTIME_DIR
        $env:SCREEN_TRANSLATE_RUNTIME_DIR = $TestDir
        & $BuiltExe --self-test-ui
        if ($LASTEXITCODE -ne 0) { throw "UI self-test failed ($LASTEXITCODE)" }
    } finally {
        $env:SCREEN_TRANSLATE_RUNTIME_DIR = $prev
        Remove-Item -Recurse -Force -Path $TestDir -ErrorAction SilentlyContinue
    }

    Write-Host "Built standalone single-file desktop app: $BuiltExe"
} finally {
    Pop-Location
}
