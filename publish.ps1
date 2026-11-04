param(
    [switch]$SelfContained = $false
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $repoRoot "src\VRLyrics.App\VRLyrics.App.csproj"

if ($SelfContained) {
    $outDir = Join-Path $repoRoot "dist\VRLyrics-win-x64-selfcontained"
    Write-Host "==> 正在构建 VRLyrics 独立自包含单文件版 (免装 .NET 运行时)..." -ForegroundColor Cyan
    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded `
        -o $outDir
}
else {
    $outDir = Join-Path $repoRoot "dist\VRLyrics-win-x64"
    Write-Host "==> 正在构建 VRLyrics 轻量单文件便携版 (依赖系统 .NET 10 Desktop Runtime)..." -ForegroundColor Cyan
    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=embedded `
        -o $outDir
}

Write-Host "`n✅ 发布成功！输出目录：$outDir" -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, @{Name="Size(KB)";Expression={[math]::Round($_.Length / 1KB, 1)}} | Format-Table -AutoSize
