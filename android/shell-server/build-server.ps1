# Build the NlueMix shell capture server.
#   Output: out\nluemix-server.jar -- a jar containing only classes.dex,
#   pushed to /data/local/tmp and executed via app_process as the shell user.
#
# NOTE: keep this file pure ASCII. Windows PowerShell 5.1 reads BOM-less UTF-8
#       as ANSI, so any non-ASCII character here gets mangled and breaks parsing.
$ErrorActionPreference = 'Stop'

$sdkRoot   = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { 'D:\BlueMix-tools\android-sdk' }
$javaHome  = if ($env:JAVA_HOME)    { $env:JAVA_HOME }    else { 'D:\BlueMix-tools\jdk\jdk-17.0.13+11' }
$javac     = Join-Path $javaHome 'bin\javac.exe'
$d8        = Join-Path $sdkRoot  'build-tools\34.0.0\d8.bat'
$androidJar= Join-Path $sdkRoot  'platforms\android-34\android.jar'

foreach ($p in @($javac, $d8, $androidJar)) {
    if (-not (Test-Path $p)) { throw "Not found: $p" }
}

$root = $PSScriptRoot
$out  = Join-Path $root 'out'
$cls  = Join-Path $out 'classes'
$dex  = Join-Path $out 'dex'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $cls, $dex | Out-Null

Write-Host '=== javac ==='
& $javac -encoding UTF-8 -source 8 -target 8 -nowarn -cp $androidJar -d $cls (Join-Path $root 'src\com\nluemix\shell\CaptureServer.java')
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }

# IMPORTANT: pass EVERY .class to d8, inner classes included.
# Passing only the outer class leaves CaptureServer$FramedSink out of the dex, and
# at runtime "sink instanceof FramedSink" then throws NoClassDefFoundError: the
# server exits after about two seconds having written 0 bytes, and nothing looks
# wrong on the adb side. This bit us twice, so the dex is verified below.
$classFiles = @(Get-ChildItem -Path $cls -Recurse -Filter '*.class' | ForEach-Object { $_.FullName })
Write-Host ("=== d8 (" + $classFiles.Count + " classes, inner classes included) ===")
$classFiles | ForEach-Object { Write-Host ("    " + (Split-Path $_ -Leaf)) }
& $d8 --min-api 29 --output $dex @classFiles
if ($LASTEXITCODE -ne 0) { throw 'd8 failed' }

$dexFile = Join-Path $dex 'classes.dex'
if (-not (Test-Path $dexFile)) { throw 'classes.dex was not produced' }

# Verify every class name is present in the dex string table. If one is missing,
# d8 dropped it -- fail the build instead of shipping a jar that crashes at runtime.
Write-Host '=== verify dex ==='
$dexText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dexFile))
foreach ($f in $classFiles) {
    $name = (Split-Path $f -Leaf) -replace '\.class$', ''
    if ($dexText -notmatch [regex]::Escape($name)) {
        throw ("classes.dex is missing " + $name + " -- d8 dropped it, runtime would throw NoClassDefFoundError")
    }
    Write-Host ("    [OK] " + $name)
}

Write-Host '=== package jar ==='
Add-Type -AssemblyName System.IO.Compression.FileSystem
$jar = Join-Path $out 'nluemix-server.jar'
Remove-Item $jar -Force -ErrorAction SilentlyContinue
$zip = [System.IO.Compression.ZipFile]::Open($jar, 'Create')
[void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dexFile, 'classes.dex', [System.IO.Compression.CompressionLevel]::Optimal)
$zip.Dispose()

Write-Host ('OK: ' + $jar + '  (' + [math]::Round((Get-Item $jar).Length / 1KB, 1) + ' KB)')
