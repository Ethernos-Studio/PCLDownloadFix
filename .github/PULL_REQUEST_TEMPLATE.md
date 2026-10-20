## 改动内容

<!-- 一句话说明这个 PR 做了什么 -->

## 原因

<!-- 关联 Issue；说明用户影响 -->

## 验证方式

- [ ] `dotnet build PCLDownloadFix.sln -c Release`
- [ ] `dotnet test tests\PCLDownloadFix.Tests\PCLDownloadFix.Tests.csproj -c Release`
- [ ] `.\tests\cli-smoke.ps1`
- [ ] 真实 PCL 集成测试（如适用，注明构建 413/414/其他）

补充说明：

## 风险与回滚

<!-- 是否修改备份/替换逻辑？失败时目标文件是否仍然可还原？ -->

## 清单

- [ ] 已更新 `CHANGELOG.md`
- [ ] 已更新 README/路线图（如行为或 CLI 契约变化）
- [ ] 未提交 PCL EXE、`bin/`、`obj/`、`dist/`、证书或用户日志
