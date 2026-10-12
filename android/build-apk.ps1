# 一键构建 NlueMix 安卓端 APK
$ErrorActionPreference = 'Continue'

$sdkRoot   = 'D:\BlueMix-tools\android-sdk'
$javaHome  = 'D:\BlueMix-tools\jdk\jdk-17.0.13+11'
$gradleBin = 'D:\BlueMix-tools\gradle\gradle-8.5\bin\gradle.bat'

if (-not (Test-Path $gradleBin)) { Write-Host "找不到 Gradle: $gradleBin" -ForegroundColor Red; exit 1 }
if (-not (Test-Path "$javaHome\bin\java.exe")) { Write-Host "找不到 JDK: $javaHome" -ForegroundColor Red; exit 1 }

$env:JAVA_HOME = $javaHome
$env:ANDROID_HOME = $sdkRoot
$env:ANDROID_SDK_ROOT = $sdkRoot
$env:PATH = "$javaHome\bin;$env:PATH"

Write-Host "=== 开始构建 APK ===" -ForegroundColor Cyan
& $gradleBin assembleDebug --no-daemon --console=plain

Write-Host ""
$apk = Get-ChildItem "$PSScriptRoot\app\build\outputs\apk\debug\*.apk" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($apk) {
    Write-Host "=== 构建成功 ===" -ForegroundColor Green
    Write-Host ("APK: " + $apk.FullName)
    Write-Host ("大小: " + [math]::Round($apk.Length / 1MB, 2) + " MB")
} else {
    Write-Host "=== 构建失败：没找到 APK ===" -ForegroundColor Red
}
