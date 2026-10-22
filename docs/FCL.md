# FCL 共存修复（v1.5.0 实验）

FCL（FoldCraftLauncher）的限制与 HMCL 同类，但实现形态完全不同：

- 上游：`FCL-Team/FoldCraftLauncher` 提交 `a36e1e4`，
  `feat: 在中国大陆地区禁用游戏下载功能并更新用户协议`
- 关键类：`com.tungsten.fcl.util.RegionUtils`
- 关键方法：
  - `isChinaMainlandMachine()`
  - `chinaMainlandNetworkAsync()`
  - `isChinaMainlandNetwork()`
- 下载页、模组详情页、联机页都调用上述判断；不是单个开关配置。

## 为什么不能像 HMCL 一样直接替换类

HMCL 是 JVM JAR，可以替换一个 `.class` 后直接运行。
FCL 是 Android APK：

1. 逻辑编译在 `classes*.dex`（Dalvik 字节码）中；
2. 修改 DEX 后原 APK 签名必然失效；
3. Android 7+ / 高 targetSdk 需要 APK Signature Scheme v2/v3；
4. 官方签名私钥不可得，只能用新证书重签；
5. 新证书与原版不同，**不能覆盖安装**，通常需要卸载原版；用户数据必须先备份。

因此 FCL 支持不能在没有 Android 签名链路的情况下安全交付。

## 候选方案

### A. APK 修改 + 重签名（用户已选择共存版）

共存版需要同时改：

- `AndroidManifest.xml` 的 `package` → `com.tungsten.fcl.fix`
- `android:label` / `app_name` → `FCL (Fix)`
- FileProvider 等 `authorities`，以及代码里通过 `BuildConfig.APPLICATION_ID` 或
  `getPackageName()` 拼接的 authority
- DEX 里的 `RegionUtils` 方法
- 重新做 v2 + v3 签名

这不是只改两个字符串：FCL 有 FileProvider、Intent、数据目录等包名派生逻辑。
二进制 APK 修改需要同时改 Manifest 二进制 XML、resources.arsc、DEX 和签名块，
任何一处漏改都可能在安装或启动时崩溃。更可靠的共存版做法是源码级 fork：

```kotlin
applicationIdSuffix = ".fix"
versionNameSuffix = " (Fix)"
resValue("string", "app_name", "FCL (Fix)")
```

并在源码中让 `RegionUtils` 返回 false，然后用 CI/Android SDK 构建签名 APK。
这需要 Android SDK/NDK，不适合塞进当前 Windows 小工具；当前工具可以在用户
提供 APK 后做二进制验证版，但不能保证所有 FCL 版本/架构都稳定。

### A0. 二进制 APK 修改 + 重签名（原计划）

DEX 侧：

- 遍历 APK 内 `classes*.dex`；
- 解析 `class_defs` / `class_data` / `method_ids`；
- 定位 `Lcom/tungsten/fcl/util/RegionUtils;` 的 `isChinaMainlandNetwork()Z`；
- 将 code_item 替换为 `const/4 v0, 0; return v0`，必要时补 nop；
- 保留 `registers_size`、try/catch 结构，只让方法直接返回 false。

签名侧：

- 方案 A1：随包提供 `apksigner.jar` + 最小 JRE，调用 `apksigner sign`；
- 方案 A2：在 C# 内实现 APK Signature Scheme v2（RSA PKCS#1 SHA-256）；
- 使用项目自签名证书（非 FCL 官方证书）。

必须明确提示：

- 不能覆盖安装官方 FCL；
- 卸载会丢数据，需先备份 `files` / 游戏目录；
- 修改版不要分发，遵守 FCL 许可证；
- FCL 更新后需要重新打补丁。

### B. LSPosed / Xposed 模块

Hook `RegionUtils.isChinaMainlandNetwork()` 返回 false。
优点：不改 APK、不丢签名、可随 FCL 更新。
缺点：需要 Root + LSPosed，受众小。

### C. 免改包：时区/网络

FCL 的 `shouldCheckNetwork()` 在设备不是中国大陆时直接返回 false：

- 系统时区不在中国大陆（如 `Asia/Singapore`），或
- 出口 IP 不在中国大陆（代理/VPN）。

这只是使用说明，不是文件修复；会改变设备时区或网络，不能由本工具静默完成。

## 当前实现（v1.5.0）

已实现并随包提供：

1. apktool 解码 FCL APK；
2. 修改 Manifest 包名、label、组件绝对名、Provider authority；
3. 修改 `RegionUtils` 的四个地区判断方法（如果该版本包含该类）；
4. 修改 `BuildConfig.APPLICATION_ID` 与其他包名常量；
5. 修改 `app_name` / `file_browser_provider` 资源；
6. apktool 重建，uber-apk-signer 使用新证书做 v2/v3 签名；
7. 输出 `*-fix.apk`，原 APK 不修改。

已验证：

- 官方稳定版 1.3.4.1 arm64：解码、共存包名/名称、资源重建、v2/v3 签名；
- **FCL main 分支 CI 构建**（包含限制提交 `a36e1e4` 之后代码）：
  - 成功找到并修改 `RegionUtils.smali`
  - `isChinaMainlandNetwork()` / `isChinaMainlandMachine()` 已变成 `const/4 v0, 0; return v0`
  - `chinaMainlandNetworkAsync()` 返回 `completedFuture(false)`
  - `observeChinaMainlandNetwork()` 直接回调 false
  - 输出包名 `com.tungsten.fcl.fix`、label `FCL (Fix)`、authority `.fix`
  - v2 + v3 签名验证通过
- 包名常量替换不会误伤 `com.tungsten.fclcore` / `com.tungsten.fcllibrary`。

尚未验证：

- 没有 Android 真机/模拟器连接，尚未验证安装、启动和实际下载页。

下一步：在 Android 设备上安装 `*-fix.apk`，确认启动和下载页。
