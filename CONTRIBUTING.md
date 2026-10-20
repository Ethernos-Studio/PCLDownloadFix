# 贡献指南

感谢你愿意改进 PCL 下载修复器。这个项目会改写另一个程序的 IL，**正确性和可回滚性优先于功能数量**。

## 开发环境

- Windows 10/11
- .NET SDK 8 或 9
- .NET Framework 4.8 Targeting Pack（Visual Studio Build Tools 或系统引用程序集）
- 可选：Visual Studio 2022（调试 WPF）

```powershell
dotnet restore PCLDownloadFix.sln
dotnet build PCLDownloadFix.sln -c Release
dotnet test tests\PCLDownloadFix.Tests\PCLDownloadFix.Tests.csproj -c Release
```

真实 PCL 集成测试默认跳过，设置以下环境变量后再运行：

```powershell
$env:PCLDF_TEST_ORIGINAL_414 = "D:\samples\PCL-2.99-414.exe"
$env:PCLDF_TEST_ORIGINAL_413 = "D:\samples\PCL-2.99-413.exe"
$env:PCLDF_TEST_PRE_412     = "D:\samples\PCL-2.14-412.exe"
dotnet test tests\PCLDownloadFix.Tests\PCLDownloadFix.Tests.csproj -c Release --filter Category=Integration
```

**不要**把 PCL 的 EXE、完整反编译结果或恢复模板以外的 PCL 代码提交到仓库。集成测试通过环境变量引用本机样本。

## 修改补丁引擎时的最低要求

1. 只允许 `RestoreSpecs.All` 中的方法发生变化；新增回调必须加入允许列表并说明来源。
2. 新增/修改恢复逻辑时，必须同时更新：
   - 单元测试或集成测试；
   - `README.md` 的工作原理/安全性说明；
   - `CHANGELOG.md`。
3. 任何写入路径都必须先写临时文件、重新加载并校验，再原子替换目标。
4. 不允许把用户的 `.pcl-original.bak` 当作可随意覆盖的临时文件；备份轮换与清单逻辑必须保持可还原。
5. 失败路径必须保证“目标文件未被替换”或“备份仍然可还原”。

## CLI/JSON 兼容性

- `--json` 模式下 stdout 只能有一个 JSON 文档；人类可读输出必须走 stderr。
- 已有字段只增不改；新增字段要更新 README 与 CLI 契约测试。
- 退出码 `0/1/2/3` 的含义保持不变。

## 代码风格

- C# 使用 4 空格缩进，文件编码 UTF-8，行尾 CRLF（见 `.editorconfig`）。
- 面向 .NET Framework 4.8，避免引入仅 .NET Core 可用的 API。
- 异常信息面向最终用户，使用中文说明“发生了什么、下一步怎么办”。
- 不要引入网络请求、遥测、后台驻留或自动更新。

## 恢复模板

`resources/RestoreDonor.dll` 由 `tools/DonorBuilder` 从限制前的官方 PCL 构建生成。重新生成前请确认：

- 源 EXE 的 SHA-256 与 README 中的 412 构建一致；
- 生成的模板通过 `DonorTests` 与真实集成测试；
- 更新 `EmbeddedResources.DonorSha256` 与相关文档。

## 提交 PR

- 一个 PR 只做一件事；修复与重构尽量分开。
- 描述中写明：改了什么、为什么、如何验证（命令/构建哈希/测试结果）。
- UI 变更请附截图；补丁行为变更请附 `verify --json` 输出。
- 不要提交 `dist/`、`bin/`、`obj/`、测试样本或签名证书。
