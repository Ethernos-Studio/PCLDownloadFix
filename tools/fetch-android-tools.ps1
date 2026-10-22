param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) "third_party\android")
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

$items = @(
    @{
        Name = "apktool.jar"
        Url = "https://gh-proxy.com/https://github.com/iBotPeaches/Apktool/releases/download/v2.11.1/apktool_2.11.1.jar"
        Hash = "56D59C524FC764263BA8D345754D8DAF55B1887818B15CD3B594F555D249E2DB"
    },
    @{
        Name = "uber-apk-signer.jar"
        Url = "https://gh-proxy.com/https://github.com/patrickfav/uber-apk-signer/releases/download/v1.3.0/uber-apk-signer-1.3.0.jar"
        Hash = "E1299FD6FCF4DA527DD53735B56127E8EA922A321128123B9C32D619BBA1D835"
    }
)

foreach ($item in $items) {
    $target = Join-Path $Destination $item.Name
    if (Test-Path $target) {
        $actual = (Get-FileHash $target -Algorithm SHA256).Hash
        if ($actual -eq $item.Hash) { Write-Host "已存在 $($item.Name)"; continue }
        Remove-Item $target -Force
    }
    Write-Host "下载 $($item.Name)..."
    curl.exe -L --ssl-no-revoke --retry 3 -o $target $item.Url
    $actual = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($actual -ne $item.Hash) { throw "$($item.Name) SHA-256 不匹配" }
}

$jreDir = Join-Path $Destination "jre"
if (!(Test-Path (Join-Path $jreDir "bin\java.exe"))) {
    $zip = Join-Path $Destination "jre17.zip"
    Write-Host "下载 Temurin JRE 17..."
    curl.exe -L --ssl-no-revoke --retry 3 -o $zip `
        "https://gh-proxy.com/https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.13%2B11/OpenJDK17U-jre_x64_windows_hotspot_17.0.13_11.zip"
    $tmp = Join-Path $Destination "jretmp"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip -DestinationPath $tmp -Force
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (Test-Path $jreDir) { Remove-Item $jreDir -Recurse -Force }
    Move-Item $inner.FullName $jreDir
    Remove-Item $tmp -Recurse -Force
    Remove-Item $zip -Force
}

Write-Host "Android 工具就绪：$Destination"
