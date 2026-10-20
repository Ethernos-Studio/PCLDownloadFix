[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$NoZip,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$propsPath = Join-Path $root "Directory.Build.props"
if (!(Test-Path $propsPath)) { throw "找不到 Directory.Build.props" }
[xml]$buildProps = Get-Content $propsPath -Raw
$version = @($buildProps.Project.PropertyGroup.Version) | Where-Object { $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "Directory.Build.props 中没有 Version" }
$distRoot = Join-Path $root "dist"
$packageName = "PCL下载修复器-第三方-v$version"
$packageDir = Join-Path $distRoot $packageName

Write-Host "==> 构建 PCLDownloadFix $version · Ethernos Studio" -ForegroundColor Cyan
dotnet build (Join-Path $root "PCLDownloadFix.sln") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }

if (!$SkipTests) {
    Write-Host "==> 运行测试" -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests\PCLDownloadFix.Tests\PCLDownloadFix.Tests.csproj") `
        -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "测试失败" }
}

$gui = Join-Path $root "src\Gui\bin\$Configuration\net48\PCLDownloadFix.Gui.exe"
$cli = Join-Path $root "src\Cli\bin\$Configuration\net48\PCLDownloadFix.Console.exe"
if (!(Test-Path $gui)) { throw "找不到 GUI 输出：$gui" }
if (!(Test-Path $cli)) { throw "找不到 CLI 输出：$cli" }

Write-Host "==> 组装发布目录" -ForegroundColor Cyan
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDir "docs") -Force | Out-Null

Copy-Item $gui (Join-Path $packageDir "PCL下载修复器.exe") -Force
Copy-Item $cli (Join-Path $packageDir "PCL下载修复器-控制台.exe") -Force
Copy-Item (Join-Path $root "README.md") $packageDir -Force
Copy-Item (Join-Path $root "CHANGELOG.md") $packageDir -Force
Copy-Item (Join-Path $root "LICENSE") $packageDir -Force
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $packageDir -Force
foreach ($extra in @("CONTRIBUTING.md", "SECURITY.md")) {
    $path = Join-Path $root $extra
    if (Test-Path $path) { Copy-Item $path $packageDir -Force }
}

$docsDir = Join-Path $root "docs"
if (Test-Path $docsDir) {
    $targetDocs = Join-Path $packageDir "docs"
    New-Item -ItemType Directory -Path $targetDocs -Force | Out-Null
    Copy-Item (Join-Path $docsDir "*") $targetDocs -Recurse -Force
}

Write-Host "==> 附带源代码" -ForegroundColor Cyan
$sourceDir = Join-Path $packageDir "source"
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
Copy-Item (Join-Path $root "src") $sourceDir -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $sourceDir "tools") -Force | Out-Null
Copy-Item (Join-Path $root "tools\DonorBuilder") (Join-Path $sourceDir "tools") -Recurse -Force
Copy-Item (Join-Path $root "tools\MethodDiff") (Join-Path $sourceDir "tools") -Recurse -Force
Copy-Item (Join-Path $root "resources") $sourceDir -Recurse -Force
Copy-Item (Join-Path $root "third_party") $sourceDir -Recurse -Force
if (Test-Path (Join-Path $root "tests")) {
    Copy-Item (Join-Path $root "tests") $sourceDir -Recurse -Force
}
Copy-Item (Join-Path $root "Directory.Build.props") $sourceDir -Force
Copy-Item (Join-Path $root "PCLDownloadFix.sln") $sourceDir -Force
Copy-Item (Join-Path $root "build.ps1") $sourceDir -Force
Get-ChildItem $sourceDir -Recurse -Directory | Where-Object { $_.Name -in @("bin", "obj") } | Remove-Item -Recurse -Force

Write-Host "==> 计算校验和" -ForegroundColor Cyan
$lines = Get-ChildItem $packageDir -File -Recurse | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $_.FullName.Substring($packageDir.Length).TrimStart('\')
    "$hash  $relative"
}
$lines | Set-Content (Join-Path $packageDir "SHA256SUMS.txt") -Encoding utf8

if (!$NoZip) {
    $zip = Join-Path $distRoot "$packageName.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host "==> 生成 $zip" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zip -CompressionLevel Optimal
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $packageName.zip" | Set-Content (Join-Path $distRoot "$packageName.zip.sha256") -Encoding utf8
}

Write-Host ""
Write-Host "完成：" -ForegroundColor Green
Write-Host "  $packageDir"
if (!$NoZip) { Write-Host "  $(Join-Path $distRoot "$packageName.zip")" }
