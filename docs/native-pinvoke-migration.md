# Natif Katman → Source-Generated P/Invoke (LibraryImport) Keşif Listesi

Tarih: 29 Ağu 2026 · Durum: keşif/liste (kod değişikliği yok)

Amaç: `WinDivertNative`'de kanıtlanan **source-generated P/Invoke**   desenini, donmuş
   yönetilen natif katmanın **tamamına** yaymak — mevcut klasik `[DllImport]`'ları taşımak
   ve **Faz 2b'de planlanan Wintun + WireGuardTunnel bağlayıcılarını doğrudan
   `[LibraryImport]` ile yazmak** (klasik `[DllImport]`'a geri dönülmez).

---

## 1. Özet

| Ölçüt | Sayı |
|---|---|
| Toplam P/Invoke site | **44** |
| Zaten `[LibraryImport]` (referans desen) | **10** — WinDivertNative (7), HotkeyManager (2), WindowsUtils/dwmapi (1) |
| Klasik `[DllImport]` — taşınacak | **34** (7 dosya) |
| Planlanan (hiç yazılmadı) — doğrudan LibraryImport | **wintun.dll + WireGuardTunnel.dll** (Faz 2b) |

Referans desen (`WinDivertNative.cs`): `partial` sınıf + `[LibraryImport]` +
`[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]` + ANSI için
`StringMarshalling.Custom/AnsiStringMarshaller` + `SetLastError=true` +
`Marshal.GetLastPInvokeError()`.

---

## 2. Klasik `[DllImport]` Envanteri — Taşınacak 34 Site

### 2.1 `ServiceLib/Common/DpapiCryptor.cs` — 3 site · zorluk: kolay
| EntryPoint | Lib | Not |
|---|---|---|
| `CryptProtectData` / `CryptUnprotectData` | crypt32 | bool dönüş + `ref DATA_BLOB` + `string` (Unicode) |
| `LocalFree` | kernel32 | IntPtr→IntPtr |

- Blocker: bool dönüşler `[return: MarshalAs(UnmanagedType.Bool)]`; LPWStr için
  `StringMarshalling.Utf16` (veya `[MarshalAs(UnmanagedType.LPWStr)]`).
- Test: `DpapiCryptorTests` mevcut (DPAPI roundtrip) — migrasyon sonrası aynen geçmeli.

### 2.2 `ServiceLib/Common/NetworkConnectionFlusher.cs` — 6 site · kolay
| EntryPoint | Lib | Not |
|---|---|---|
| `GetExtendedTcpTable` / `GetExtendedUdpTable` | iphlpapi | `bool sort` parametresi + `IntPtr`/`ref int` |
| `SetTcpEntry` ×2 (IPv4/IPv6 row) | iphlpapi | `ref` struct |
| `DnsFlushResolverCache` / `DnsFlushResolverCacheEntry_W` | dnsapi | bool dönüş → `MarshalAs(Bool)` |

### 2.3 `ServiceLib/Common/WindowsNetworkTable.cs` — 2 site · kolay (konsolidasyon)
- `GetExtendedTcpTable` / `GetExtendedUdpTable` — **2.2 ile birebir aynı imza**:
  tek bir natif katmanda birleştirilmeli (çift tanım kaldırılır).

### 2.4 `ServiceLib/Handler/SysProxy/ProxySettingWindows.cs` — 2 site · orta/zor
| EntryPoint | Lib | Not |
|---|---|---|
| `InternetSetOption` | WinInet | `CharSet.Auto` + bool |
| `RasEnumEntries` | Rasapi32 | `CharSet.Auto` + `[In,Out] RASENTRYNAME[]` (ByValTStr içinde) + `ref int` ×2 |

- **Blocker: `CharSet.Auto` LibraryImport'ta desteklenmez** → A/W açıkça seçilmeli
  (`InternetSetOptionW`, `RasEnumEntriesW`); RASENTRYNAME dizisi için
  `[MarshalAs(UnmanagedType.LPArray, SizeParamIndex = …)]` gerekebilir.

### 2.5 `ServiceLib/Services/Gpn/GpnProcessTree.cs` — 4 site · kolay
| EntryPoint | Lib | Not |
|---|---|---|
| `CreateToolhelp32Snapshot` | kernel32 | IntPtr |
| `Process32FirstW` / `Process32NextW` | kernel32 | Unicode + `ref PROCESSENTRY32` (ByValTStr) + bool |
| `CloseHandle` | kernel32 | bool |

- Desen zaten LibraryImport dostu (W sonekleri + bool marshal mevcut) — sadece atr. taşı.

### 2.6 `ServiceLib/Services/WindowsJobService.cs` — 4 site · kolay
| EntryPoint | Lib | Not |
|---|---|---|
| `CreateJobObject` | kernel32 | Unicode + `string? lpName` (nul geçilir) |
| `SetInformationJobObject` | kernel32 | bool + `nint` ×3 |
| `AssignProcessToJobObject` | kernel32 | bool |
| `CloseHandle` | kernel32 | bool |

### 2.7 `AoGPN/AoGPN/Views/MainWindow.xaml.cs` — 13 site · orta
| EntryPoint | Lib | Not |
|---|---|---|
| `ReleaseCapture`, `SetCapture` | user32 | bool |
| `SendMessage` | user32 | **`CharSet.Auto` → `SendMessageW`** açık entry point |
| `GetWindowLongW` / `SetWindowLongW` | user32 | W sonekli EntryPoint — olduğu gibi taşınır |
| `MonitorFromWindow`, `GetMonitorInfo`, `GetWindowRect`, `GetClientRect`, `ClientToScreen`, `SetWindowPos`, `GetCursorPos`, `GetDpiForWindow` | user32 | bool/ref/out struct — desteklenir |

---

## 3. Planlanan: Wintun + WireGuardTunnel (Faz 2b) — Doğrudan LibraryImport

Kodda **henüz hiçbir bağlayıcı yok** (yalnızca roadmap). `wintun.dll` şu an xray
paketinin içinde TUN için kopyalanıyor; `WireGuardTunnel.dll` projede yok. İkisi de
**yazılırken `[LibraryImport]` ile başlanacak** (klasik `[DllImport]` yazılmayacak).

### 3.1 `wintun.dll` — cdecl (WinDivertNative deseni)
| EntryPoint (yaklaşık) | Not |
|---|---|
| `WintunCreateAdapter` | `LPCWSTR` ×2 + **GUID by-value** (blittable struct) |
| `WintunOpenAdapter` / `WintunCloseAdapter` | HANDLE = `nint` |
| `WintunDeleteDriver` | — |
| `WintunGetAdapterLUID` | `out WINTUN_ADAPTER_LUID` (16 bayt, blittable) |
| `WintunGetRunningDriverVersion` | uint dönüş |
| `WintunStartSession` / `WintunEndSession` | `WINTUN_MAX_RING_CAPACITY` |
| `WintunGetReadWaitEvent` | `out HANDLE` (bekleyen okuma için) |
| `WintunReceivePacket` / `WintunReleaseReceivePacket` | `out DWORD` boyut + `nint` paket ptr |
| `WintunAllocateSendPacket` / `WintunSendPacket` | `nint` paket ptr |

Not: tümü cdecl; paket tamponları `nint` ile taşınır, yönetilen kopya yalnızca
kanala yazarken.

### 3.2 `WireGuardTunnel.dll` (wireguard-windows tunnel DLL) — cdecl (header'dan doğrula)
| EntryPoint (kesin imza header'dan doğrulanmalı) | Not |
|---|---|
| `WireGuardTunnelSetConfig` | `in WIREGUARD_TUNNEL_CONFIG` — **struct içi function-pointer alanları** (`delegate* unmanaged[Cdecl]<…>`, blittable) + `WINTUN_ADAPTER*` |
| `WireGuardTunnelGetSessionStatus` | `in/out WIREGUARD_TUNNEL_SESSION_STATUS` — sabit boyutlu peer durum dizileri (`ByValArray`) |
| `WireGuardTunnelSetLoggerCallback` | callback — `[UnmanagedCallersOnly]` static + `IntPtr`, veya `UnmanagedFunctionPointer` delegate |

Not: `WIREGUARD_TUNNEL_CONFIG`/`_SESSION_STATUS` kesin düzeni `tunnel.h`'den
kopyalanmalı (pointer boyutu 4/8'e duyarlı — `nint`/`delegate*` kullanımıyla
otomatik). Logger callback'i cdecl/stdcall farkı header'dan doğrulanır.

### 3.3 `WinDivert.dll` — çalışma zamanı dağıtımı (P/Invoke değil, paketleme)
`WinDivertNative` (`ServiceLib/Services/Gpn/`) zaten `[LibraryImport]` kullanır;
buradaki eksik dağıtım tarafıydı — hiçbir paketleme `WinDivert.dll`'i kopyalamıyordu
ve GPN yakalama köprüsü her oturumda `Unable to load DLL 'WinDivert.dll'` ile
ölüyordu (canlı ölçüm: `GPN_BRIDGE yakalama döngüsü fault: ... 0x8007007E`).

Çözüm (MsBuild — `AoGPN.csproj` → `PromoteWinDivertToOutputRoot`):

* Resmi WinDivert 2.2.2 dağıtımı **repo'ya gömülüdür** (`Libs/WinDivert/`
  `WinDivert-2.2.2-A.zip` — `basil00/WinDivert`, LGPL-3.0, lisans metni
  `WinDivert-LICENSE.txt` olarak paketlenir; köken + SHA-256 için
  `Libs/WinDivert/README.md`). Build önce bu zip'ten açar (çevrimdışı,
  deterministik), zip yoksa indirme fallback'i kullanır (TLS 1.2 açıkça set
  edilir — PowerShell 5.1 aksi takdirde GitHub'da başarısız olur). exe yanına
  kopyalanan parçalar: x64 → `WinDivert.dll` + `WinDivert64.sys`; x86 build →
  ayrıca `WinDivert32.sys`. Hata build'i kırmaz (bir sonraki build yeniden dener;
  wintun konvansiyonuyla aynı).
* **Sürücü kurulumu ayrı adım gerektirmez**: WinDivert dokümantasyonuna göre
  sürücü, ilk `WinDivertOpen()` çağrısında `WinDivert64.sys`'ten **sessizce ve
  otomatik** kurulur — yeter ki işlem yönetici olsun (GPN TUN yolu zaten yönetisel
  ayrıcalık ister, bu yüzden pratikte sağlanır).
* Hata haritalaması `WinDivertEngine.MissingDriverHint`'te: 2 (driver dosyası
  yok), 126 (DLL/bağımlılığı yok — ayrıca `DllNotFoundException` anlamlı mesaja
  sarılır), 5 (yönetici yok), 577 (imza), 1275 (güvenlik yazılımı/virtüel), 1753
  (BFE kapalı).
* **Çevre sağlığı + dashboard uyarısı:** `WinDivertHealthMonitor`
  (`ServiceLib/Services/Gpn/`) exe yanındaki `WinDivert.dll`/`WinDivert64.sys`
  varlığını, `\\.\WinDivert` cihaz probe'unu ve (yöneticiyse) SCM kurulumunu
  yönetir — `WinDivertDriverSupport` ile windivert.c'deki `WinDivertDriverInstall`
  akışını birebir izler (servisi kalıcı bırakır; WinDivertOpen kendi transient
  modelinde siley de çalışır). Sonuç `AppEvents.WinDivertHealthChanged` →
  dashboard sarı bant + `WINDIVERT env state=...` diag satırı; uygulama açılışında
  ve köprü fault'unda tetiklenir.
* **API uyumluluğu (canlı doğrulandı)**: resmî 2.2.2 ikilisi `WinDivertOpenEx`'i
  ve fork/OpenEx'e özgü flag bitlerini (`QUEUE_*` vb.) İÇERMEZ; flag değerleri de
  resmî windivert.h ile birebir olmalıdır (`RECV_ONLY=0x4`, `SEND_ONLY=0x8`,
  `NO_INSTALL=0x10`, `FRAGMENTS=0x20` — eski sabitler fork biçimliydi ve resmî
  sürücüde 87'ye takılırdı). `WinDivertEngine.OpenEx` export yoksa klasik
  `WinDivertOpen`'a düşer: fork bitleri `ClassicFlagMask` ile süzülür, kuyruk
  ayarları resmî `WinDivertSetParam` (QUEUE_LENGTH/TIME/SIZE — id'ler 0/1/2
  resmîdir) ile uygulanır. `WINDIVERT_ADDRESS` 80-bayt düzeni resmî 2.2.2 ile
  birebir uyumluydu (doğrulandı — bit alanları da aynı).
* CI: `wintun-driver-smoke.yml` artık WinDivert dosyalarını da test çıktısına
  indirir — `WinDivertDriverSmokeTests` gerçek sürücüye karşı koşar.

---

## 4. Blocker / Dönüşüm Kontrol Listesi

1. **`CharSet.Auto` yok** → A/W açıkça seçilir: `InternetSetOptionW`,
   `RasEnumEntriesW`, `SendMessageW`. (Blocker sayısı: 3 site)
2. **bool dönüşler** → `[return: MarshalAs(UnmanagedType.Bool)]` (LibraryImport
   varsayılanı Win32 BOOL'a uyar ama açık işaretlenir; `sort` gibi bool
   **parametreler** için `[MarshalAs(UnmanagedType.Bool)]` gerekir).
3. **ANSI (LPStr)** → `StringMarshalling.Custom + AnsiStringMarshaller`
   (WinDivertNative deseni; varsayılan UTF-8 değildir).
4. **UTF-16 (LPWStr)** → `StringMarshalling.Utf16` veya `[MarshalAs(LPWStr)]`.
5. **Struct by-ref/out, ByValTStr, ByValArray, GUID by-value** → blittable,
   desteklenir; `SizeConst` korunur.
6. **Callback'ler** → `[UnmanagedCallersOnly]` static + `IntPtr`; struct **içindeki**
   function pointer'lar → `delegate* unmanaged[Cdecl]<…>` (blittable).
7. **`SetLastError=true`** desteklenir; hata `Marshal.GetLastPInvokeError()`.
8. **cdecl** → `[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]`;
   stdcall gereken tek nokta (WireGuardTunnel callback) header'dan netleşir.
9. **Sınıf `partial`** olmalı (generator kod üretir).
10. **Variadic kullanım yok** (doğrulandı) — engel değil.
11. **Test edilebilirlik**: natif çağrılar zaten `IWinDivertApi`-benzeri arayüzle
    sarılabilir; DpapiCryptor/GpnProcessTree gibi katmanlar test altında (sahte
    yoksa bile) Windows dışı platformda çağrılmadığı için yalnızca derleme doğrulanır.

---

## 5. Önerilen Taşıma Sırası

1. **Kolay (ServiceLib, bağımsız):** `DpapiCryptor` → `WindowsJobService` →
   `GpnProcessTree` → `NetworkConnectionFlusher` (+ `WindowsNetworkTable` ile
   konsolidasyon). Her adımda `dotnet build` + ilgili testler
   (`DpapiCryptorTests`, Gpn/GPN süiti).
2. **Orta:** `MainWindow.xaml.cs` user32 bloğu (13 site; `SendMessageW` seçimi).
3. **Zor:** `ProxySettingWindows` (`Auto`→W + RASENTRYNAME dizisi).
4. **Faz 2b:** `WintunNative` + `WireGuardTunnelNative` **doğrudan** `[LibraryImport]`
   ile yazılır (yukarıdaki kontrol listesine uyarak) — klasik `[DllImport]`'a dönülmez.

---

## 6. İlgili Yol Haritası

- Faz 2a: `WinDivertNative` ✅ (LibraryImport referansı) — `docs/gpn-migration-roadmap.md`
- Faz 2b: `WireGuardTunnelService` (Wintun adaptörü + WireGuard oturumu) — bu listeye
  göre bindings doğrudan source-generated yazılır.
