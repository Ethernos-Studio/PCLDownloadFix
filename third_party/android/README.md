# Android tools for FCL coexistence patching

These binaries are intentionally not committed (large / third-party):

- `apktool.jar` 2.11.1 — Apache-2.0
- `uber-apk-signer.jar` 1.3.0 — Apache-2.0
- `jre/` — Eclipse Temurin JRE 17 — GPLv2 + Classpath Exception

They are copied into the release `tools/android/` directory by `build.ps1`.

Download:

```powershell
.\tools\fetch-android-tools.ps1
```

Checksums (SHA-256):

```text
56D59C524FC764263BA8D345754D8DAF55B1887818B15CD3B594F555D249E2DB  apktool.jar
E1299FD6FCF4DA527DD53735B56127E8EA922A321128123B9C32D619BBA1D835  uber-apk-signer.jar
```

FCL output is a new package (`com.tungsten.fcl.fix`) signed with the signer's
debug certificate. It cannot update over the official FCL; install it as a
separate app and move game directories manually.
