# v2rayN 5.39 for Windows XP

A Windows XP-compatible adaptation of [2dust/v2rayN](https://github.com/2dust/v2rayN), based on upstream version 5.39.

### How to use
- Use a Windows XP-compatible x86 V2Ray/Xray core in the same folder as `v2rayN.exe`.
- Run `v2rayN.exe`.

### Requirements
- Windows XP **Service Pack 3** (32-bit).
- Microsoft .NET Framework 4.0.
- Microsoft .NET Framework 4 update **KB2468871** (required by the .NET 4.0 compatibility assemblies used by this build).

### XP compatibility notes
- The application is built for .NET Framework 4.0 and x86.
- gRPC traffic statistics are disabled in this XP build.
- The upstream V2Ray/Xray core releases are not guaranteed to run on Windows XP. Use core binaries specifically built for XP.
- Windows XP's built-in Schannel does not provide the TLS 1.2 support available on newer Windows versions. HTTPS access to services that require TLS 1.2 may fail unless a separate TLS-capable proxy/provider is used.

### Upstream projects
- [v2rayN](https://github.com/2dust/v2rayN)
- [v2fly core](https://github.com/v2fly/v2ray-core/releases)
- [Xray core](https://github.com/XTLS/Xray-core/releases)

### Telegram Channel
[github_2dust](https://t.me/v2rayn)
