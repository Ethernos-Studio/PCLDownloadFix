# 发布清单

## 1. 版本与变更

1. 修改 `Directory.Build.props` 中的 `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion`。
2. 在 `CHANGELOG.md` 增加对应版本段落。
3. 更新 README 中受影响的版本表、状态说明与截图（如界面有变化）。
4. 确认 `resources/RestoreDonor.dll` 未变化；若变化，更新 `EmbeddedResources.DonorSha256` 与 `THIRD-PARTY-NOTICES.txt`。

## 2. 本地验证

```powershell
dotnet restore PCLDownloadFix.sln
dotnet build PCLDownloadFix.sln -c Release --nologo
dotnet test tests\PCLDownloadFix.Tests\PCLDownloadFix.Tests.csproj -c Release --no-build --nologo
.\tests\cli-smoke.ps1
.\build.ps1
```

如果本机有真实 PCL 样本，额外运行：

```powershell
$env:PCLDF_TEST_ORIGINAL_414 = "D:\samples\PCL-2.99-414.exe"
$env:PCLDF_TEST_ORIGINAL_413 = "D:\samples\PCL-2.99-413.exe"
$env:PCLDF_TEST_PRE_412     = "D:\samples\PCL-2.14-412.exe"
dotnet test tests\PCLDownloadFix.Tests\PCLDownloadFix.Tests.csproj -c Release --no-build --filter Category=Integration
```

手工冒烟：

1. 复制 414 到临时目录，`patch` → 确认 PCL 下载页可打开；
2. `restore` → 确认哈希回到原值；
3. 把 413 覆盖到同一路径，再次 `patch` → 确认旧备份归档、可正常还原；
4. 用 `--dry-run` 和 `--output` 各跑一次，确认原文件未被修改；
5. 复制 412 到临时目录，`disable-update` 后确认 `PCL\Setup.ini` 写入 `SystemSystemUpdate:3`，再 `enable-update` 恢复原值；
6. 打开“批量模式”，粘贴或拖入 412/414，检测状态后处理：412 应关闭自动更新，414 应修复下载。

## 3. 代码签名（推荐）

需要 Authenticode 代码签名证书（OV/EV）。**不要把证书或密码提交到仓库。**

```powershell
$pfx = "C:\secure\codesign.pfx"
$password = Read-Host -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))

signtool sign /fd SHA256 /f $pfx /p $plain `
  /tr http://timestamp.digicert.com /td SHA256 `
  "dist\PCL下载修复器-第三方-v1.3.2\PCL下载修复器.exe" `
  "dist\PCL下载修复器-第三方-v1.3.2\PCL下载修复器-控制台.exe"

signtool verify /pa /v "dist\PCL下载修复器-第三方-v1.3.2\PCL下载修复器.exe"
```

签名后必须**重新生成** `SHA256SUMS.txt` 与 ZIP（`build.ps1` 会重算，但若在打包后签名，请手动更新校验和）。

## 4. 打包与校验

```powershell
.\build.ps1
Get-Content .\dist\PCL下载修复器-第三方-v1.3.2\SHA256SUMS.txt
Get-FileHash .\dist\PCL下载修复器-第三方-v1.3.2.zip -Algorithm SHA256
```

确认 ZIP 中不包含 `bin/`、`obj/`、`.pclfix.json`、测试样本或证书。

## 5. GitHub Release

1. 推送标签：`git tag v1.3.2 && git push origin v1.3.2`。
2. `.github/workflows/release.yml` 会自动构建、测试、打包、生成来源证明并创建 Release。
3. 检查 Release 附件：
   - `PCL下载修复器-第三方-v1.3.2.zip`
   - `.zip.sha256`
   - 包内 `SHA256SUMS.txt`
4. 在 Release 说明中写清楚：支持的 PCL 构建、已知问题、是否已签名、SHA-256。

## 6. 杀毒软件误报

签名后如仍有误报，向 Microsoft Defender、火绒、360 等提交样本与说明：

- 工具用途、源码地址、签名指纹；
- 不联网、不驻留、不写启动项；
- 行为是用户主动选择的本地 IL 修补；
- 附上 `SHA256SUMS.txt` 与 VirusTotal 链接。

## 7. 发布后

- 关注 Issue 中的 PCL 新版本反馈，必要时更新支持矩阵；
- 把新的 PCL 构建加入私有集成测试样本；
- 若 PCL 改变限制方式，按 `docs/ROADMAP.md` 的“版本韧性”处理。
