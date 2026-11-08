# FlashDraft Core

Protocol, transport, cryptography, tests and shared documentation for FlashDraft - LAN and Internet file transfer tool.

FlashDraft Core 是 FlashDraft（局域网与互联网文件快传工具）的协议、传输、加密、测试与共享文档仓库。

## What this repository contains / 本仓内容

| Project | Responsibility |
|---|---|
| `FlashDraft.Core` | Protocol constants, models, frame header, connection code, service abstractions |
| `FlashDraft.Crypto` | X25519 key exchange, HKDF-SHA256 derivation, AES-GCM chunk cipher, nonce builder |
| `FlashDraft.Transport` | UDP multicast discovery, mDNS fallback, LAN TCP transfer, WAN UDP hole punching, resume writer |
| `FlashDraft.Core.Tests` | xUnit unit tests and two-endpoint loopback integration tests |

The package `FlashDraft.Core` is published to nuget.org on tag `v0.1.0`. Client repositories reference it by version.

## Architecture / 架构

MVVM + layered design. The transport layer never references Avalonia. Progress is reported as immutable snapshots through `IProgress<T>` and a `Channel`, and the ViewModel marshals updates onto the UI thread.

- LAN: UDP multicast on `239.255.70.90`, fixed port, heartbeat every 3 seconds; mDNS `_flashdraft._udp.local` as fallback; TCP parallel streams for data.
- WAN: public STUN for reflexive addresses, manually exchanged connection code, UDP hole punching. TURN is an interface only (no server is operated).
- Security: ephemeral X25519 per session, system CSPRNG entropy, HKDF-SHA256 directional keys, AES-GCM, AAD bound to the frame header, 256 KiB chunks with per-chunk SHA-256.
- Resume: `.part` file plus sibling `.meta` storing a chunk bitmap; file is renamed only after full verification.

## Build / 构建

Use the latest stable .NET SDK. The target framework is centralized in `Directory.Build.props`.

```bash
dotnet restore FlashDraft.sln
dotnet build FlashDraft.sln -c Release
dotnet test FlashDraft.sln -c Release
```

`FlashDraft.Crypto` restores `BouncyCastle.Cryptography` for managed X25519. `FlashDraft.Transport` currently restores no external mDNS package; the fallback provider is an adjustable stub (see comments in `Discovery.cs`).

## Supported runtimes / 运行环境

- Windows 10-11
- macOS 14 or later
- Ubuntu 22.04 or later
- Android 8.0 (API 26) or later

## Distribution / 分发

Native installers are unsigned at zero cost. Each release ships SHA-256 checksums and OS-bypass instructions. Windows may show SmartScreen; macOS Gatekeeper may require right-click Open; Linux deb/rpm usually installs without prompts. See each client repository README for steps.

安装包暂不签名，随版本提供 SHA-256 校验和与系统放行步骤。Windows 可能出现 SmartScreen，macOS 可能需要右键打开，Linux deb/rpm 通常无提示。

## Contributing / 贡献

See `CONTRIBUTING.md` (DCO required) and `CODE_OF_CONDUCT.md`.

## PCL notice / PCL 声明

FlashDraft's user interface design is inspired by Plain Craft Launcher (PCL) by 龙腾猫跃 (Meloong-Git/PCL). This project contains no PCL source code, images, logos, or fonts. FlashDraft is an independent file-transfer tool and is not affiliated with, endorsed by, or officially related to PCL or its author. PCL and all related rights remain with the original author. See https://github.com/Meloong-Git/PCL/blob/main/LICENCE.

FlashDraft 的界面设计受龙腾猫跃的 Plain Craft Launcher (PCL) 启发。本项目不包含 PCL 的源代码、图片、Logo 或字体。FlashDraft 是独立的文件传输工具，与 PCL 及其作者无关联、非官方作品、未获其背书。PCL 及相关权利归原作者所有。参见 https://github.com/Meloong-Git/PCL/blob/main/LICENCE。

## License / 许可

Own code is Apache-2.0. See `LICENSE`.
