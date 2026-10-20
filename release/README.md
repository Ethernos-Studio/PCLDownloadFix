# 发布文件

本目录包含 v1.0.0 的预编译程序：

- `PCL下载修复器.exe`：图形界面版
- `PCL下载修复器-控制台.exe`：控制台版

SHA-256 见同目录 `SHA256SUMS.txt`，也可执行：

```powershell
Get-FileHash .\PCL下载修复器.exe -Algorithm SHA256
.\PCL下载修复器-控制台.exe selfcheck
```

源码位于仓库 `src` 目录，可按根目录 `README.md` 的“构建”一节重新编译。
