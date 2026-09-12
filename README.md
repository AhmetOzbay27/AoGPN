# AO GPN — Gaming Private Network

### A modern VPN/GPN client for Windows, Linux and macOS. Maintained by **Ahmet Özbay**. Supports [Xray](https://github.com/XTLS/Xray-core) and [sing-box](https://github.com/SagerNet/sing-box) and [others](https://github.com/AhmetOzbay27/AoGPN/wiki/List-of-supported-cores)

[![CodeFactor](https://www.codefactor.io/repository/github/AhmetOzbay27/aogpn/badge)](https://www.codefactor.io/repository/github/AhmetOzbay27/aogpn)
[![Release](https://img.shields.io/github/v/release/AhmetOzbay27/AoGPN?logo=github&label=Release)](https://github.com/AhmetOzbay27/AoGPN/releases)
[![Downloads](https://img.shields.io/github/downloads/AhmetOzbay27/AoGPN/latest/total?logo=github&label=Downloads)](https://github.com/AhmetOzbay27/AoGPN/releases)
[![Telegram](https://img.shields.io/badge/Telegram-Chat-26A5E4?logo=telegram)](https://t.me/aogpn)
 
[![Windows](https://img.shields.io/badge/Windows-supported-0078D6?logo=windows)](https://github.com/AhmetOzbay27/AoGPN) 
[![Linux](https://img.shields.io/badge/Linux-supported-FCC624?logo=linux&logoColor=000)](https://github.com/AhmetOzbay27/AoGPN) 
[![macOS](https://img.shields.io/badge/macOS-supported-000000?logo=apple)](https://github.com/AhmetOzbay27/AoGPN) 
[![GPG Signed](https://img.shields.io/badge/GPG-signed-4B32C3?logo=gnuprivacyguard)](https://github.com/AhmetOzbay27/AoGPN)

---

## AO GPN — Oyunun İçin Özel Ağ

> **Gecikme, oyunda kaybettiren tek şeydir.** AO GPN, oyun trafiğini akıllı bir
> tünelden geçirerek pingini ölçülebilir şekilde düşüren, oyuncular için
> sıfırdan yazılmış modern bir GPN/VPN istemcisi.
>
> **Sadece oyunun tünellenir, gerisi karışmaz.** Global VPN'in aksine AO GPN,
> oyunların trafiğini yönlendirirken diğer uygulamalarını doğrudan bağlantıda
> bırakır. Oyunu listene ekle, tüneli aç — gerisini o yönetir. Hatta listede
> bir oyun açıldığında bağlantı kendiliğinden kurulur, son oyun kapanınca kapanır.
>
> **Övünerek söylemiyoruz, ölçüyoruz.** Her oyun kartında bağlantıdan önceki
> ve sonraki *gerçek* oyun sunucusu gecikmesini görürsün: `42 ms → 18 ms (−24 ms)`.
> İyileşme yeşil, kötüleşme kırmızı — rakamlar değil, sonuçlar konuşur.
>
> **Akıllı bağlantı.** WireGuard, Reality/TLS, Hysteria2/TUIC ve OpenVPN
> protokollerini tek tıkla yönet; otomatik mod, sana en düşük gecikmeyi veren
> sunucuyu canlı ölçümlerle seçsin. Sunucu düşerse saniyeler içinde yedeğine
> geçer — sen maçtan kopmazsın.
>
> **Kontrol merkezin avucunda.** Camdan bir dashboard: hangi programın nereye,
> hangi rota üzerinden bağlandığını canlı izle. 25 tema ve 3 tamamen farklı
> arayüz tasarımı (skin) ile görünüm senin.
>
> **Herkesin dili, her platform.** Türkçe dahil 9 dil; Windows, Linux ve
> macOS'ta çalışır. Çekirdek ve güncelleme indirmeleri SHA-256 ile doğrulanır,
> sürümler GPG ile imzalanır — güven pazarlıksız.
>
> **AO GPN.** Sadece kazanmak için yapıldı. 🎮
>
> 🔗 github.com/AhmetOzbay27/AoGPN · Topluluk: t.me/AoGPN

---

## Features

- **Modern WebView2 Dashboard** — Real-time connection center with live telemetry, IP verification, protocol strategy selector, and per-app traffic monitoring.
- **Global VPN Mode** — All traffic routed through TUN with automatic transport detection and admin-elevation checks.
- **GPN Game Tunnel** — Per-app split tunneling: only assigned games and applications are routed through the VPN, with a one-click Whitelist/Blacklist direction switch.
- **25 Gaming Themes** — Choose from Nebula, Inferno, Synthwave, Cyberpunk, Matrix, Crimson, Velocity, Aurora, Neon Cyber, Crimson Core, and more, with animated cursor effects, sound engines, and confetti celebration on connect.
- **Standalone Skins** — Swap the whole dashboard for an independent design: NEXUS GPN, CYBER, or INFRA run in an isolated iframe and stay live through a safe bridge to the app state.
- **Real Per-Game Latency** — Dashboard boost cards show the actual game-server ping measured before (direct) and after (tunnel) the GPN connection, color-coded by quality, with the before → after delta.
- **Single Instance** — Second launch brings the existing window to front with a tray notification.
- **ISP IP Baseline** — Your real ISP IP is cached at startup and displayed in the dashboard before connecting.
- **Multi-Language** — English, Chinese (Simplified/Traditional), Turkish, Persian, French, Hungarian, Indonesian, and Russian.
- **Multi-Protocol** — Automatic, WireGuard, Mimic Reality/TLS, Hysteria2/TUIC, and OpenVPN support.
- **Smart Connection Logic** — Automatic best-server selection with live failover, tiered WireGuard UDP → V2ray TCP fallback, REALITY core switching, and auto-reconnect.
- **GlassWire-Style Monitor** — Live connection table showing which program connects where, through which route.
- **Node Management** — Ping all nodes, edit, disable/delete, deduplicate, and pull new nodes from your own link pool — all from the dashboard.
- **System Proxy Integration** — Independent proxy preference preserved alongside active TUN connections, plus a proxy-only mode that works even without a tunnel.
- **Game Auto-Connect Trigger** — Automatically switches to GPN mode when a listed game starts running.

---

## Download / 下载

Download the latest release here:

在这里下载最新版本：

[https://github.com/AhmetOzbay27/AoGPN/releases](https://github.com/AhmetOzbay27/AoGPN/releases)


> [!TIP]
> AO GPN is the desktop version. For the mobile version, please visit AoGPNG \
> AO GPN 是电脑版，手机版请访问 AoGPNG
>
> https://github.com/AhmetOzbay27/AoGPNG

---

## Documentation / 使用文档

Read the Wiki for usage guides and configuration details.

请阅读 Wiki 获取使用说明和配置教程。

[https://github.com/AhmetOzbay27/AoGPN/wiki](https://github.com/AhmetOzbay27/AoGPN/wiki)

---

## Release Notes & Versioning / 版本说明与发布规范

- **Change log / 变更日志** — full per-version history, newest first:
  [CHANGELOG.md](CHANGELOG.md)
- **Versioning & release guide** (Türkçe) — the git tag is the single source of
  the version (the release pipeline stamps it into the build), how the
  changelog milestones are organised, and how to keep the dashboard's language
  files and Release Notes tab in sync: [RELEASE_YONERGESI.md](RELEASE_YONERGESI.md)

---

## Supported Platforms / 支持平台

| Platform / 平台 | x64 | x86 | arm64 | riscv64 | loong64 |
| --- | --- | --- | --- | --- | --- |
| Windows | ✅ | ✅ | ✅ | - | - |
| Linux | ✅ | - | ✅ | ✅ | ✅ |
| macOS | ✅ | - | ✅ | - | - |

---

## GPG Verification / GPG 签名校验

Release files are signed with GPG to verify authenticity and integrity, helping prevent mirror, ISP, or CDN hijacking.

发布文件已使用 GPG 签名，可用于校验文件真实性与完整性，预防镜像站、运营商或 CDN 劫持。

### Fingerprint / 公钥指纹

```
7694 5E9F 3E9A 168F 8070 F195 805D 661C
134D FAF6 8903 C199 463C 31E5 AE90 3AE0
```

---

## Community / 社区

Telegram Group / Telegram 群组：

[https://t.me/AoGPN](https://t.me/AoGPN)

Telegram Channel / Telegram 频道：

[https://t.me/AoGPN](https://t.me/AoGPN)

---

## Maintainer / Sorumlu

**Ahmet Özbay** — AO GPN Project Maintainer
