param(
    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $repoRoot "src\VRLyrics.App\VRLyrics.App.csproj"

$running = Get-Process -Name "VRLyrics" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "==> 检测到正在运行的 VRLyrics.exe，正在自动停止以更新可执行文件..." -ForegroundColor Yellow
    try {
        $evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\VRLyrics_YieldToElevated_Event")
        $evt.Set()
        $evt.Dispose()
        Start-Sleep -Milliseconds 600
    } catch {}
    Get-Process -Name "VRLyrics" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

if ($SelfContained) {
    $outDir = Join-Path $repoRoot "dist\VRLyrics-win-x64-selfcontained"
    Write-Host "==> 正在构建 VRLyrics 独立自包含单文件版 (免装 .NET 运行时)..." -ForegroundColor Cyan
    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -o $outDir
}
else {
    $outDir = Join-Path $repoRoot "dist\VRLyrics-win-x64"
    $lockedExe = Join-Path $outDir "VRLyrics.exe"
    $oldExe = Join-Path $outDir "VRLyrics.exe.old"
    Remove-Item $oldExe -Force -ErrorAction SilentlyContinue
    if ((Get-Process -Name "VRLyrics" -ErrorAction SilentlyContinue) -and (Test-Path $lockedExe)) {
        Move-Item $lockedExe $oldExe -Force -ErrorAction SilentlyContinue
    }
    Write-Host "==> 正在构建 VRLyrics 轻量单文件便携版 (依赖系统 .NET 10 Desktop Runtime)..." -ForegroundColor Cyan
    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=embedded `
        -o $outDir
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败 (ExitCode: $LASTEXITCODE)"
}

Write-Host "`n[OK] 发布成功！输出目录：$outDir" -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, @{Name="Size(KB)";Expression={[math]::Round($_.Length / 1KB, 1)}} | Format-Table -AutoSize
