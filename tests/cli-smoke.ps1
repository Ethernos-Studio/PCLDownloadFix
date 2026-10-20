#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root "src\Cli\bin\$Configuration\net48\PCLDownloadFix.Console.exe"
if (!(Test-Path $cli)) { throw "找不到 CLI：$cli（请先构建）" }

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("pcldf-cli-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$stderrFile = Join-Path $tempRoot "stderr.txt"
$badFile = Join-Path $tempRoot "not-pcl.txt"

function Invoke-Cli {
    param([string[]]$Arguments)
    $stdout = & $cli @Arguments 2> $stderrFile
    $exitCode = $LASTEXITCODE
    $stdoutText = ($stdout -join "`n").Trim()
    $stderrText = (Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue)
    if ($null -eq $stderrText) { $stderrText = "" }
    return [pscustomobject]@{
        ExitCode = $exitCode
        StdOut   = $stdoutText
        StdErr   = $stderrText.Trim()
    }
}

function Assert-Json {
    param($Text)
    try { return $Text | ConvertFrom-Json }
    catch { throw "stdout 不是有效 JSON：`n$Text" }
}

try {
    Set-Content -Path $badFile -Value "not a .NET assembly" -Encoding UTF8

    $result = Invoke-Cli @("verify", $badFile, "--yes", "--json")
    if ($result.ExitCode -ne 1) { throw "无效文件的退出码应为 1，实际为 $($result.ExitCode)" }
    if ($result.StdErr -ne "") { throw "JSON 模式下 stderr 应为空：$($result.StdErr)" }
    $json = Assert-Json $result.StdOut
    if ($json.ok -ne $false) { throw "无效文件应返回 ok=false" }
    if ($json.command -ne "verify") { throw "command 字段不正确" }
    if ($json.exitCode -ne 1) { throw "exitCode 字段不正确" }

    $result = Invoke-Cli @("verify", $badFile, "--json")
    if ($result.ExitCode -ne 3) { throw "未确认免责声明的退出码应为 3，实际为 $($result.ExitCode)" }
    if ($result.StdErr -ne "") { throw "JSON 模式下 stderr 应为空：$($result.StdErr)" }
    $json = Assert-Json $result.StdOut
    if ($json.ok -ne $false -or $json.exitCode -ne 3) { throw "未确认免责声明的 JSON 不正确" }

    $pcl414 = $env:PCLDF_TEST_ORIGINAL_414
    if (![string]::IsNullOrWhiteSpace($pcl414) -and (Test-Path $pcl414)) {
        $result = Invoke-Cli @("verify", $pcl414, "--yes", "--json")
        if ($result.ExitCode -ne 0) { throw "414 校验失败：$($result.StdOut)" }
        $json = Assert-Json $result.StdOut
        if ($json.state -ne "Original") { throw "414 状态应为 Original，实际为 $($json.state)" }
        if ($result.StdErr -ne "") { throw "JSON 模式下 stderr 应为空：$($result.StdErr)" }
    }

    Write-Host "CLI 冒烟测试通过。" -ForegroundColor Green
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# The last native command in this script intentionally exits non-zero (JSON error
# contract tests). PowerShell would otherwise propagate that exit code even though
# all assertions passed, failing the CI step.
exit 0
