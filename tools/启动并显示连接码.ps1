# 启动 NlueMix 并告诉你连接码
# 用脚本自身的位置定位发布目录，换台电脑/换目录都不用改（原来写死了绝对路径，找不到 exe）
$dir = (Resolve-Path (Join-Path $PSScriptRoot '..\publish')).Path
$exe = Join-Path $dir 'NlueMix.exe'
$log = Join-Path $dir 'nluemix.log'

# 已经在跑就不重复启动
$running = Get-Process -Name NlueMix -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'NlueMix 已经在运行了。' -ForegroundColor Yellow
} else {
    Write-Host '正在启动 NlueMix…' -ForegroundColor Cyan
    Start-Process -FilePath $exe
    Start-Sleep -Seconds 8
}

Write-Host ''
if (Test-Path $log) {
    $line = Get-Content $log -ErrorAction SilentlyContinue | Select-String '连接码' | Select-Object -Last 1
    if ($line) {
        Write-Host '===================================' -ForegroundColor Green
        Write-Host $line.ToString().Trim() -ForegroundColor Green
        Write-Host '===================================' -ForegroundColor Green
    } else {
        Write-Host '日志里没找到连接码，看界面右上角。' -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host '最近日志:' -ForegroundColor Cyan
    Get-Content $log -Tail 8
} else {
    Write-Host '还没有日志文件（NlueMix 可能还没跑起来）' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '下一步测试：' -ForegroundColor Cyan
Write-Host '  cd ' -NoNewline; Write-Host $dir
Write-Host '  (把下面这行的 XXXX 换成上面的连接码)'
Write-Host '  node "..\tools\模拟手机.js" XXXX 20'
