# AoGPN WireGuard Sunucu Kurulumu — Oracle Linux 9 (dnf + firewalld)

> Hedef: ExitLag benzeri bir oyun VPN'i için kiralanan sunucuda (Oracle Cloud
> Compute) WireGuard tüneli + BBR/fq + NAT masquerade ile istemcinin tüm
> trafiğini en düşük gecikmeyle hedefe ulaştırmak.
>
> Bu rehber **canlı sunucuda uçtan uca doğrulanmıştır**: el sıkışma + tünel içi
> ping + internet çıkışı (8.8.8.8'e ~3ms, 0 kayıp) başarıyla test edildi.

---

## Kısa notlar (başlamadan önce)

- **OS:** Oracle Linux **9.x** (UEK kernel) — bu rehber `dnf` + **firewalld**
  kullanır. **Ubuntu/Debian veya `iptables-persistent` için değildir.**
- **Selinux:** `Enforcing` iken sorun yok — wg0 soketi ve forwarding normal
  çalışır; ayrıca denesel taviz gerekmez.
- **VCN giriş kuralı şart:** Uç noktadan istemci bağlanamaz → bkz. §5.
- **MTU:** WireGuard 60 bayt başlık ekler; 1500'lük bağlantıda **1420**
  parçalanmayı önleyen stabil değerdir. Ağda takılma olursa her iki tarafta
  **1384** → **1280**'e inmek gerekebilir.
- **Idempotent script:** Aşağıdaki script mevcut anahtarları **korumaz**;
  aynı anahtar dosyaları varsa yenilerini üretir. Korumak istersen anahtar
  üretim satırlarını atla (bkz. §6).

---

## 0) Sunucuya bağlan (Windows PowerShell)

```powershell
ssh -i "C:\Users\Ahmet Özbay\Downloads\ssh-key-2026-08-28.key" opc@92.4.220.236
```

Anahtar güvenliği hatası (`UNPROTECTED PRIVATE KEY FILE`) alırsan:

```powershell
icacls "C:\Users\Ahmet Özbay\Downloads\ssh-key-2026-08-28.key" /inheritance:r /grant:r "$($env:USERNAME):(R)"
```

---

## 1) Kurulum scripti

Aşağıdaki scripti sunucuda bir dosyaya kopyalayıp çalıştır (ör. `bash aogpn-setup.sh`).

Mevcut projede bu scriptin birebir test edilmiş hâli şurada durur:
`.freebuff/aogpn-server-setup.sh` — `scp aogpn-server-setup.sh opc@92.4.220.236:/tmp/`
ile yükleyip `bash /tmp/aogpn-server-setup.sh` çalıştırabilirsin.

```bash
#!/usr/bin/env bash
# AOGPN — WireGuard oyun VPN sunucu kurulumu (Oracle Linux 9, dnf + firewalld)
set -euo pipefail

echo "==> [1/6] wireguard-tools kuruluyor..."
if ! sudo dnf install -y wireguard-tools > /tmp/aogpn-dnf.log 2>&1; then
  echo "    baseos'ta yok, EPEL deneniyor..."
  sudo dnf install -y epel-release > /tmp/aogpn-dnf.log 2>&1 || \
    sudo dnf install -y https://dl.fedoraproject.org/pub/epel/epel-release-latest-9.noarch.rpm > /tmp/aogpn-dnf.log 2>&1
  sudo dnf install -y wireguard-tools > /tmp/aogpn-dnf.log 2>&1
fi
command -v wg > /dev/null && echo "    wireguard-tools OK ($(wg --version))"

echo "==> [2/6] Kernel ayarlari: BBR + fq + ip_forward..."
sudo tee /etc/sysctl.d/99-aogpn.conf > /dev/null <<'EOF'
# AOGPN oyun VPN cekirdek ayarlari (Oracle Linux)
net.ipv4.ip_forward = 1
net.core.default_qdisc = fq
net.ipv4.tcp_congestion_control = bbr
net.ipv4.tcp_mtu_probing = 1
net.ipv4.tcp_notsent_lowat = 16384
net.ipv4.tcp_slow_start_after_idle = 0
net.core.rmem_max = 2500000
net.core.wmem_max = 2500000
EOF
sudo sysctl --system > /dev/null
echo "    sysctl uygulandi"

echo "==> [3/6] WireGuard anahtarlar ve yapilandirma..."
sudo mkdir -p /etc/wireguard && sudo chmod 700 /etc/wireguard
SERVER_PRIV=$(sudo sh -c 'umask 077; wg genkey | tee /etc/wireguard/server_private.key')
CLIENT_PRIV=$(sudo sh -c 'umask 077; wg genkey | tee /etc/wireguard/client_private.key')
SERVER_PUB=$(printf '%s' "$SERVER_PRIV" | wg pubkey)
CLIENT_PUB=$(printf '%s' "$CLIENT_PRIV" | wg pubkey)
sudo chmod 600 /etc/wireguard/server_private.key /etc/wireguard/client_private.key

WAN_IF=$(ip route show default | awk '{print $5}' | head -n1)
echo "    WAN arayuzu: $WAN_IF"

sudo tee /etc/wireguard/wg0.conf > /dev/null <<EOF
[Interface]
Address = 10.66.66.1/24
ListenPort = 51820
PrivateKey = $SERVER_PRIV
MTU = 1420
SaveConfig = false

[Peer]
PublicKey = $CLIENT_PUB
AllowedIPs = 10.66.66.2/32
EOF
sudo chmod 600 /etc/wireguard/wg0.conf

sudo tee /etc/wireguard/client.conf > /dev/null <<EOF
[Interface]
Address = 10.66.66.2/24
PrivateKey = $CLIENT_PRIV
MTU = 1420
DNS = 1.1.1.1

[Peer]
PublicKey = $SERVER_PUB
Endpoint = 92.4.220.236:51820
AllowedIPs = 0.0.0.0/0, ::/0
PersistentKeepalive = 25
EOF
sudo chmod 600 /etc/wireguard/client.conf
sudo cp /etc/wireguard/client.conf /tmp/aogpn-client.conf
sudo chmod 644 /tmp/aogpn-client.conf
echo "    anahtarlar + wg0.conf + client.conf hazir"

echo "==> [4/6] firewalld: port 51820/udp + masquerade + trusted zone..."
sudo firewall-cmd --permanent --add-port=51820/udp > /dev/null
sudo firewall-cmd --permanent --zone=public --add-masquerade > /dev/null
sudo firewall-cmd --permanent --zone=trusted --add-interface=wg0 > /dev/null
sudo firewall-cmd --reload > /dev/null 2>&1 || true

echo "==> [5/6] wg0 baslatiliyor + acilista otomatik..."
sudo systemctl enable wg-quick@wg0 > /dev/null 2>&1 || true
sudo systemctl restart wg-quick@wg0
echo "    wg0 aktif"

echo "==> [6/6] Kendi kendine test (netns + gecici peer)..."
SELFTEST_DIR=/tmp/aogpn-selftest
sudo rm -rf "$SELFTEST_DIR" && sudo mkdir -p "$SELFTEST_DIR"
# test istemcisi GERCEK wg0 sunucu anahtarini kullanmali (SERVER_PUB)
sudo sh -c "umask 077; wg genkey > $SELFTEST_DIR/cli.key"
TCLI_PRIV=$(sudo cat "$SELFTEST_DIR/cli.key")
TCLI_PUB=$(printf '%s' "$TCLI_PRIV" | wg pubkey)

sudo wg set wg0 peer "$TCLI_PUB" allowed-ips 10.66.66.3/32

sudo ip netns add wgtest 2>/dev/null || true
sudo ip netns del wgtest 2>/dev/null || true
sudo ip netns add wgtest
sudo ip link add vt0 type veth peer name vt1
sudo ip link set vt1 netns wgtest
sudo ip netns exec wgtest ip addr add 10.99.0.2/24 dev vt1
sudo ip netns exec wgtest ip link set vt1 up
sudo ip netns exec wgtest ip link set lo up
sudo ip addr add 10.99.0.1/24 dev vt0
sudo ip link set vt0 up
sudo firewall-cmd --zone=trusted --add-interface=vt0 > /dev/null 2>&1 || true
sudo ip netns exec wgtest ip route add default via 10.99.0.1

sudo tee "$SELFTEST_DIR/test-client.conf" > /dev/null <<EOF
[Interface]
Address = 10.66.66.3/24
PrivateKey = $TCLI_PRIV
MTU = 1420

[Peer]
PublicKey = $SERVER_PUB
Endpoint = 10.99.0.1:51820
AllowedIPs = 0.0.0.0/0, ::/0
PersistentKeepalive = 25
EOF

set +e
sudo ip netns exec wgtest wg-quick up "$SELFTEST_DIR/test-client.conf"
UP_RC=$?
sudo wg show
echo "--- tunel ici ping (10.66.66.1) ---"
sudo ip netns exec wgtest ping -c 2 -W 3 10.66.66.1
PING1_RC=$?
echo "--- internet ping (8.8.8.8) ---"
sudo ip netns exec wgtest ping -c 3 -W 4 8.8.8.8
PING2_RC=$?
set -e

sudo ip netns exec wgtest wg-quick down "$SELFTEST_DIR/test-client.conf" 2>/dev/null || true
sudo ip netns del wgtest 2>/dev/null || true
sudo ip link del vt0 2>/dev/null || true
sudo wg set wg0 peer "$TCLI_PUB" remove
sudo rm -rf "$SELFTEST_DIR"

if [ "$UP_RC" -eq 0 ] && [ "$PING1_RC" -eq 0 ] && [ "$PING2_RC" -eq 0 ]; then
  echo ""
  echo "################ SELF-TEST: BASARILI ✔ ################"
else
  echo ""
  echo "################ SELF-TEST: BASARISIZ ✘ ################"
  echo "# UP_RC=$UP_RC PING1=$PING1_RC PING2=$PING2_RC #"
fi

echo ""
echo "================ KURULUM OZETI ================"
sudo wg show
echo "--- kernel ---"
sysctl net.ipv4.tcp_congestion_control net.core.default_qdisc net.ipv4.ip_forward
echo "--- firewalld (public) ---"
sudo firewall-cmd --list-all
echo "--- firewalld (trusted) ---"
sudo firewall-cmd --zone=trusted --list-all
echo "--- client.conf /tmp/aogpn-client.conf icinde (scp ile al) ---"
```

---

## 2) Neler yapılıyor (adım adım)

| # | Parça | Ne yapar |
|---|---|---|
| 1 | `wireguard-tools` | Yönetim araçları (`wg`, `wg-quick`). Oracle baseos'ta yoksa EPEL'e düşer. |
| 2 | `/etc/sysctl.d/99-aogpn.conf` | `ip_forward=1` (çekirdekte IP yönlendirme), **BBR** + **fq** (gecikme/jitter dalgalanmasını önler), MTU probing ve bellek tamponları. |
| 3 | Anahtarlar + `wg0.conf` + `client.conf` | Sunucu adresi `10.66.66.1/24`, port 51820, MTU 1420. `client.conf` **tüm trafiği** tünelden gönderir (`AllowedIPs = 0.0.0.0/0, ::/0`). |
| 4 | firewalld | UDP 51820 açılır (kalıcı), `public` zone'da **masquerade** (NAT), `wg0` arayüzü `trusted` zone'a alınır (gelen paketler kabul + forwarding). |
| 5 | `wg-quick@wg0` | Arayüzü başlatır ve açılışta otomatik çalıştırır. |
| 6 | Self-test | `netns` + geçici veth ile gerçek bir istemci simüle eder: tünel içi ve internet çıkışını doğrular — **sunucuyu sunucusuz doğrulamanın yolu.** |

---

## 3) Tuzağa düşme: "el sıkışma kurulamıyor" vakaları

Doğrulama sırasında karşılaşılan iki tipik tuzak ve çözümü:

1. **Self-test'te yanlış sunucu anahtarı.** Test istemcisine `/etc/wireguard/server_private.key`
   yerine taze üretilmiş bir "sahte sunucu anahtarı" yazılırsa el sıkışma sessizce
   atılır (`transfer 0 B received`, hiçbir `endpoint` görünmez). Test istemcisinin
   `Peer PublicKey` değeri **gerçek wg0 genel anahtarı** olmalıdır.
2. **Netns/veth yolu OCIRA'ları.** veth paketleri host'un ana soketine masquerade
   olmadan ulaşamayabilir; self-testte veth, host tarafında `trusted` zone'a
   alınarak (ve endpoint **dahili IP** kullanılarak hairpin NAT'tan kaçınılarak)
   izole edilir.

İstemciden gelen gerçek trafik **harici** IP üzerinden INPUT olarak geldiği için
bu test sorunları kurulumun kendisinde yaşanmaz; yalnızca self-test metodolojisiyle
ilgilidir.

---

## 4) Doğrulama komutları (sunucuda)

```bash
sudo wg show                    # peer, endpoint, latest handshake, transfer
sysctl net.ipv4.tcp_congestion_control net.core.default_qdisc net.ipv4.ip_forward
sudo firewall-cmd --list-all     # 51820/udp + masquerade görünmeli
sudo firewall-cmd --zone=trusted --list-all   # wg0 + vt0 (sadece wg0 kalmalı)
cat /etc/wireguard/client.conf   # Peer PublicKey == wg0 genel anahtarı
```

İstemci bağlandıktan (Activate) sonra burada **`latest handshake`** görünmelidir.

---

## 5) Oracle VCN giriş kuralı (şart)

Console → **Networking → Virtual cloud networks** → senin VCN (örn.
`vcn-20260828-2253`) → **Subnets** → varsayılan subnet → **Security Lists** →
**Default Security List** → **Add Ingress Rules**:

- Source Type: `CIDR` · Source CIDR: `0.0.0.0/0`
- IP Protocol: `UDP` · Destination Port Range: `51820`

Instance'a ayrıca bir Network Security Group bağlıysa aynı kuralı oraya da ekle.

---

## 6) Windows istemcisi

```powershell
scp -i "C:\Users\Ahmet Özbay\Downloads\ssh-key-2026-08-28.key" opc@92.4.220.236:/tmp/aogpn-client.conf "$env:USERPROFILE\Downloads\aogpn-client.conf"
```

1. [wireguard.com/install](https://www.wireguard.com/install/) → **WireGuard for Windows** kur.
2. **Import tunnel(s) from file** → `aogpn-client.conf` → **Activate**.
3. Kontrol (PowerShell): `ping 10.66.66.1` → birkaç ms; `ping 8.8.8.8` → sunucu
   bölgesinden çıkış. Sunucuda `sudo wg show` → `latest handshake` görünür.

MTU takılması olursa her iki `MTU =` satırını 1420 → 1384 → (gerekirse) 1280 yap
ve tüneli **iki tarafta da** yeniden başlat.

---

## 7) Bakım

- **Yeni istemci ekleme:** `wg genkey` ile yeni özel anahtar üret, genel anahtarını
  `wg0.conf`'a yeni bir `[Peer]` bloğu olarak ekle (`AllowedIPs = 10.66.66.N/32`),
  `wg-quick@wg0` yeniden başlat, istemciye kendi özel anahtarıyla bir `client.conf`
  ver.
- **Anahtarları koruyarak scripti tekrar çalıştırma:** script her zaman yeni anahtar
  üretir. Korumak istersen `[3/6]` bölümündeki `wg genkey` satırlarını
  `if [ -f /etc/wireguard/server_private.key ]; then SERVER_PRIV=$(sudo cat ...); else ... fi`
  deseniyle değiştir (`.` freebuff/aogpn-server-setup.sh` zaten böyledir).
- **MTU değişimi:** `wg0.conf` ve `client.conf` içindeki `MTU` satırını güncelle,
  tüneli yeniden başlat.

---

## 8) Özet

| Bileşen | Değer |
|---|---|
| OS | Oracle Linux 9 (dnf) |
| Ağ aracı | firewalld (iptables-persistent değil) |
| Tünel | WireGuard (`wg-quick@wg0`) |
| VPN ağı | `10.66.66.0/24` (sunucu .1, istemci .2) |
| Port | UDP 51820 |
| MTU | 1420 |
| CC | BBR + fq |
| NAT | firewalld masquerade (public zone) |
| Doğrulama | Self-test ✔ (tünel içi 0.2ms, internet 3.3ms, 0 kayıp) |