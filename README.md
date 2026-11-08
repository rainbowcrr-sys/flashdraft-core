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


## Public Internet discovery and transfer / 公网发现与传输

There is no signaling server, so peers cannot discover each other automatically on the public Internet. LAN peers are found through the UDP multicast and the LAN candidates carried in the offer code; WAN peers must exchange that code out of band.

本项目没有信令服务器，因此公网上**无法自动发现对端**。局域网靠 UDP 组播与连接码内的内网候选地址发现；跨网必须带外交换连接码。

Steps / 操作步骤:

1. Peer A generates an offer. It contains A's ephemeral X25519 public key, LAN candidates, a STUN reflexive address (obtained from a public STUN server), a one-time token and a five-minute expiry.
   A 生成连接码，内含 A 的临时 X25519 公钥、内网候选地址、公共 STUN 服务器返回的反射地址、一次性 token 与五分钟过期时间。
2. A sends the `flashdraft://` code to B through any external channel: QR code, pasted text or a chat message. The QR code and the 6-digit PIN are confirmation aids, not discovery mechanisms.
   A 通过任意带外渠道把 `flashdraft://` 连接码交给 B：二维码、粘贴文本或聊天消息。二维码与 6 位 PIN 只是确认手段，不是发现机制。
3. Both peers generate their own offer and exchange codes, then switch on WAN mode. Each side derives the same session id and directional keys from its own private key and the peer's public key.
   两端各自生成连接码并互相交换，然后开启公网模式。双方各自用本机私钥与对端公钥派生出相同的会话 ID 与双向密钥。
4. The sender punches a UDP hole toward the reflexive address in the peer's code; the responder answers inbound probes. Encrypted 32 KiB chunks then flow over stop-and-wait ACKs with bounded retransmission, per-chunk SHA-256 and final file verification.
   发送端向对端码里的反射地址发起 UDP 打洞，接收端应答探测包。随后以带 ACK 与有限重传的停等方式传输 32 KiB 加密分块，逐块 SHA-256 校验，最后整文件校验后落盘。
5. If hole punching fails (typically symmetric NAT or a restrictive firewall), the app reports it explicitly and asks for a fresh offer code, a LAN, or manual port mapping. A TURN relay interface is reserved but deliberately not implemented.
   打洞失败（常见于对称 NAT 或严格防火墙）时应用会明确提示，并要求重新交换连接码、改用局域网或手动端口映射。TURN 中继仅保留接口，刻意不实现。

Security / 安全性: offers carry public keys only, never private keys. Session keys are ephemeral X25519 -> HKDF-SHA256 derived, and every chunk is AES-GCM sealed with the frame header as AAD. Logging stays local and no telemetry is sent.

连接码只含公钥，绝无私钥。会话密钥由临时 X25519 经 HKDF-SHA256 派生，每个分块以帧头为 AAD 做 AES-GCM 加密。日志仅本地保存，不上报任何数据。

## Contributing / 贡献

See `CONTRIBUTING.md` (DCO required) and `CODE_OF_CONDUCT.md`.

## PCL notice / PCL 声明

FlashDraft's user interface design is inspired by Plain Craft Launcher (PCL) by 龙腾猫跃 (Meloong-Git/PCL). This project contains no PCL source code, images, logos, or fonts. FlashDraft is an independent file-transfer tool and is not affiliated with, endorsed by, or officially related to PCL or its author. PCL and all related rights remain with the original author. See https://github.com/Meloong-Git/PCL/blob/main/LICENCE.

FlashDraft 的界面设计受龙腾猫跃的 Plain Craft Launcher (PCL) 启发。本项目不包含 PCL 的源代码、图片、Logo 或字体。FlashDraft 是独立的文件传输工具，与 PCL 及其作者无关联、非官方作品、未获其背书。PCL 及相关权利归原作者所有。参见 https://github.com/Meloong-Git/PCL/blob/main/LICENCE。

## License / 许可

Own code is Apache-2.0. See `LICENSE`.
