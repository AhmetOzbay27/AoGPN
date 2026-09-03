<#
.SYNOPSIS
    Proxy-only egress dogrulama senaryosu (uctan uca).

    Sistem proxy'sine saygi duyan uygulamalarin (tarayici, curl, oyun istemcisi...)
    "Proxy-only" moddayken secili dugum uzerinden ciktigini dogrular:
      1. On kosullar  -> sistem proxy'si ayarli mi, core calisiyor mu, port dinliyor mu
      2. Baglanti     -> dogrudan vs. proxy uzerinden egress IP karsilastirmasi
                         (HTTP + SOCKS5 + sistem proxy'sine saygi duyan gercek test)
      3. Core log     -> cekirdegin access logunda test isteklerinin gorunmesi
                         + hata logunda REALITY/handshake hatalarinin yakalanmasi

.DESCRIPTION
    AoGPN "Proxy-only" modda (baglanti KAPALI + sistem proxy tercihi Set/PAC) yerel
    bir SOCKS/HTTP dinleyicisi calistirir ve sistem proxy'sini o dinleyiciye yonlendirir.
    Bu script, o dinleyici uzerinden yapilan isteklerin gercekten secili dugumden cikip
    cikmadigini ve cekirdek logunun bunu dogrulayip dogrulamadigini uctan uca kontrol eder.

    Bilinen durum: gomulu Xray 26.3.27, yeni 3x-ui sunucularinin REALITY
    minClientVer kapisina takilabilir (v2rayN'nin patch'li 26.6.1'i calisir).
    Boylesi bir durumda baglanti testleri timeout olur ve error logda
    "REALITY: received real certificate" gibi bir satir gorunur; script bunu acikca raporlar.

.PARAMETER AppDir
    AoGPN'nin calisma dizini (guiConfigs / guiLogs / binConfigs iceren klasor).
    Bos birakilirsa otomatik aranir (repo bin klasorleri + calisan AoGPN process yolu).

.PARAMETER Port
    Yerel proxy portu. 0 ise guiConfigs\config.json'dan LocalPort okunur (varsayilan 10808).

.PARAMETER Target
    Egress IP test hedefi (varsayilan https://api.ipify.org).

.PARAMETER Verbose
    Ayrintili cikti.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\verify-proxy-only-egress.ps1 -AppDir "D:\Programlar\VPN\AoGPN\AoGPN\AoGPN\bin\Release\net10.0-windows10.0.19041"
#>
param(
    [string]$AppDir = "",
    [int]$Port = 0,
    [string]$Target = "https://api.ipify.org",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$targetHost = ([uri]$Target).Host

# ---------------------------------------------------------------------------
# Cikti yardimcilari
# ---------------------------------------------------------------------------
$script:passCount = 0
$script:failCount = 0
$script:warnCount = 0
$script:results = [System.Collections.Generic.List[object]]::new()

function Write-Step([string]$title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }
function Write-Info([string]$msg)   { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Pass([string]$name, [string]$detail = "") {
    $script:passCount++
    $script:results.Add([pscustomobject]@{ Check = $name; Result = "PASS"; Detail = $detail })
    Write-Host ("  [PASS] " + $name + $(if ($detail) { " - " + $detail } else { "" })) -ForegroundColor Green
}
function Write-Fail([string]$name, [string]$detail = "") {
    $script:failCount++
    $script:results.Add([pscustomobject]@{ Check = $name; Result = "FAIL"; Detail = $detail })
    Write-Host ("  [FAIL] " + $name + $(if ($detail) { " - " + $detail } else { "" })) -ForegroundColor Red
}
function Write-Warn([string]$name, [string]$detail = "") {
    $script:warnCount++
    $script:results.Add([pscustomobject]@{ Check = $name; Result = "WARN"; Detail = $detail })
    Write-Host ("  [WARN] " + $name + $(if ($detail) { " - " + $detail } else { "" })) -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# AppDir cozumu
# ---------------------------------------------------------------------------
function Resolve-AppDir {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($AppDir) { $candidates.Add($AppDir) }
    if ($PSScriptRoot) {
        foreach ($tfm in @("net10.0-windows10.0.19041", "net8.0-windows10.0.19041")) {
            foreach ($conf in @("Release", "Debug")) {
                $candidates.Add((Join-Path $PSScriptRoot "AoGPN\AoGPN\bin\$conf\$tfm"))
            }
        }
    }
    # Calisan AoGPN process yolundan
    try {
        Get-Process -Name "AoGPN" -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Path) { $candidates.Add((Split-Path $_.Path -Parent)) }
        }
    } catch { }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "guiConfigs\config.json"))) {
            return (Resolve-Path $c).Path
        }
    }
    return ""
}

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " AoGPN Proxy-Only Egress Dogrulama (end-to-end)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$resolvedDir = Resolve-AppDir
if (-not $resolvedDir) {
    Write-Warn "AppDir" "guiConfigs\config.json bulunamadi. -AppDir <klasor> parametresi verin."
} else {
    Write-Info "AppDir: $resolvedDir"
}

$configPath = if ($resolvedDir) { Join-Path $resolvedDir "guiConfigs\config.json" } else { "" }

# ---------------------------------------------------------------------------
# 1. On kosullar: proxy-only aktif mi?
# ---------------------------------------------------------------------------
Write-Step "1) On kosullar (proxy-only aktif mi)"

$sysProxyType = $null
$localPort = 0
if ($configPath -and (Test-Path $configPath)) {
    try {
        $cfg = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $sysProxyType = $cfg.SystemProxyItem.SysProxyType   # PowerShell property erisimi buyuk/kucuk harfe duyarsiz
        if (-not $sysProxyType) { $sysProxyType = $cfg.systemProxyItem.sysProxyType }
        $localPort = [int]($cfg.Inbound[0].LocalPort)
        if (-not $localPort) { $localPort = [int]($cfg.inbound[0].localPort) }
        if (-not $localPort) { $localPort = 10808 }
        Write-Info "config.json: SysProxyType=$sysProxyType (0=Clear 1=Set 2=Unchanged 3=Pac), LocalPort=$localPort"
    } catch {
        Write-Warn "config.json" "Okunamadi: $($_.Exception.Message)"
        $localPort = 10808
    }
} else {
    $localPort = 10808
}
if ($Port -gt 0) { $localPort = $Port }

# 1a. Kaydedilmis proxy tercihi
if ($null -eq $sysProxyType) {
    Write-Warn "Kaydedilmis proxy tercihi" "config.json okunamadi; tercih kontrolu atlandi"
} elseif ($sysProxyType -eq 1 -or $sysProxyType -eq 3) {
    Write-Pass "Proxy tercihi (Set/Pac)" "SysProxyType=$sysProxyType"
} else {
    Write-Fail "Proxy tercihi (Set/Pac)" "SysProxyType=$sysProxyType -> Proxy-only icin Set(1) veya Pac(3) gerekli. Ayarlar/dashboard'dan 'Sistem Proxy' -> Set/Pac secin ve baglantinin KAPALI oldugundan emin olun."
}

# 1b. Sistem proxy'si (kayit defteri)
try {
    $is = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -ErrorAction Stop
    $proxyServer = $is.ProxyServer
    if ($is.ProxyEnable -eq 1 -and $proxyServer -match "127\.0\.0\.1:$localPort") {
        Write-Pass "Sistem proxy'si (kayit defteri)" "ProxyEnable=1, ProxyServer=$proxyServer"
    } elseif ($is.ProxyEnable -eq 1) {
        Write-Fail "Sistem proxy'si (kayit defteri)" "ProxyEnable=1 ama ProxyServer='$proxyServer' beklenen 127.0.0.1:$localPort degil"
    } else {
        Write-Fail "Sistem proxy'si (kayit defteri)" "ProxyEnable=0 -> proxy-only mod aktif degil. Dashboard'dan sistem proxy Set/Pac yapin."
    }
} catch {
    Write-Warn "Sistem proxy'si (kayit defteri)" "Okunamadi: $($_.Exception.Message)"
}

# 1c. Core process
$coreProc = $null
try {
    $coreProc = Get-Process -Name "xray","sing-box" -ErrorAction SilentlyContinue |
        Where-Object { if ($resolvedDir) { $_.Path -and $_.Path.StartsWith($resolvedDir, [System.StringComparison]::OrdinalIgnoreCase) } else { $true } } |
        Select-Object -First 1
} catch { }
if ($coreProc) {
    Write-Pass "Core process" "$($coreProc.ProcessName) PID=$($coreProc.Id)"
} elseif ($resolvedDir) {
    $anyCore = Get-Process -Name "xray","sing-box" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($anyCore) {
        Write-Warn "Core process" "xray/sing-box calisiyor ama AppDir altinda bulunamadi (baska bir uygulamaya ait olabilir, or. v2rayN). PID=$($anyCore.Id)"
    } else {
        Write-Fail "Core process" "xray.exe / sing-box.exe calismiyor -> proxy-only core baslatilamamis olabilir. Dashboard'dan sistem proxy Set/Pac yapin ve uygulama loglarina bakin."
    }
} else {
    Write-Warn "Core process" "AppDir bilinmedigi icin dogrulanamadi"
}

# 1d. Port dinliyor mu
$listening = $false
try {
    $client = [System.Net.Sockets.TcpClient]::new()
    $iar = $client.BeginConnect("127.0.0.1", $localPort, $null, $null)
    $listening = $iar.AsyncWaitHandle.WaitOne(2000) -and $client.Connected
    $client.Close()
} catch { $listening = $false }
if ($listening) {
    Write-Pass "Yerel dinleyici" "127.0.0.1:$localPort dinliyor"
} else {
    Write-Fail "Yerel dinleyici" "127.0.0.1:$localPort yanit vermiyor -> core ayakta degil veya farkli port"
}

# ---------------------------------------------------------------------------
# 2. Baglanti testleri (egress IP)
# ---------------------------------------------------------------------------
Write-Step "2) Baglanti testleri (egress IP)"

$curl = "curl.exe"
if (-not (Get-Command $curl -ErrorAction SilentlyContinue)) {
    Write-Fail "curl" "curl.exe bulunamadi (Windows 10+ ile gelir)"
    exit 1
}

function Invoke-Curl([string]$label, [string[]]$extraArgs, [int]$timeout = 20) {
    $argList = @("-sS", "--max-time", "$timeout") + $extraArgs + @($Target)
    $out = & $curl @argList 2>&1
    if ($LASTEXITCODE -ne 0 -or -not $out) {
        return [pscustomobject]@{ Ok = $false; Ip = ""; Raw = ($out -join " ") }
    }
    return [pscustomobject]@{ Ok = $true; Ip = ([string]$out).Trim(); Raw = "" }
}

# 2a. Dogrudan (proxy'siz) - temel cizgi
$direct = Invoke-Curl "direct" @()
if ($direct.Ok) {
    Write-Info "  dogrudan egress IP : $($direct.Ip)"
} else {
    Write-Warn "Dogrudan baglanti" "internete erisilemedi: $($direct.Raw)"
}

# 2b. HTTP proxy uzerinden
$http = Invoke-Curl "http" @("-x", "http://127.0.0.1:$localPort")
if ($http.Ok) {
    Write-Info "  HTTP proxy egress  : $($http.Ip)"
} else {
    Write-Fail "HTTP proxy baglantisi" "127.0.0.1:$localPort uzerinden baglanilamadi: $($http.Raw)"
}

# 2c. SOCKS5 proxy uzerinden
$socks = Invoke-Curl "socks" @("-x", "socks5h://127.0.0.1:$localPort")
if ($socks.Ok) {
    Write-Info "  SOCKS5 egress      : $($socks.Ip)"
} else {
    Write-Fail "SOCKS5 baglantisi" "socks5h://127.0.0.1:$localPort uzerinden baglanilamadi: $($socks.Raw)"
}

# 2d. Sistem proxy'sine saygi duyan gercek istemci (WinINET)
#     Invoke-RestMethod, Windows sistem proxy ayarlarini kullanir (tarayici gibi).
$sys = [pscustomobject]@{ Ok = $false; Ip = ""; Raw = "" }
$is2 = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -ErrorAction SilentlyContinue
if ($is2.ProxyEnable -eq 1) {
    try {
        $ip = (Invoke-RestMethod -Uri $Target -TimeoutSec 20 -ErrorAction Stop).ToString().Trim()
        $sys = [pscustomobject]@{ Ok = $true; Ip = $ip; Raw = "" }
        Write-Info "  sistem proxy egress : $ip  (Invoke-RestMethod/WinINET)"
    } catch {
        $sys = [pscustomobject]@{ Ok = $false; Ip = ""; Raw = $_.Exception.Message }
    }
} else {
    Write-Warn "Sistem proxy istemci testi" "ProxyEnable=0 oldugundan atlandi"
}

# 2e. Karsilastirma
$proxied = @($http, $socks, $sys) | Where-Object { $_.Ok } | ForEach-Object { $_.Ip } | Select-Object -Unique
if ($proxied.Count -eq 0) {
    Write-Fail "Egress dogrulama" "Proxy uzerinden hicbir istek basarili olmadi -> asagidaki core log/hata bolumune bakin (buyuk ihtimalle REALITY/core versiyon sorunu)."
} elseif ($proxied.Count -gt 1) {
    Write-Fail "Egress dogrulama" "Farkli proxy yollarindan farkli egress IP'ler cikti: $($proxied -join ', ') -> yollardan biri sizinti yapiyor olabilir."
} else {
    $egressIp = @($proxied)[0]   # tek degerde [0] karakter degil, degerin kendisi olsun
    if ($direct.Ok -and $egressIp -eq $direct.Ip) {
        # Makine dugumun kendisi olabilir (kendi sunucunu test etmek) -> IP karsilastirmasi ayirt edici degil.
        Write-Warn "Egress dogrulama" "Proxy egress IP, dogrudan egress IP ile ayni ($($egressIp)). Makine dugumun kendisi ise bu normaldir; kesin kanit icin asagidaki core access log kaydina bakin (istek logda gorunuyorsa dugumden gecmistir)."
    } else {
        Write-Pass "Egress dogrulama" "Proxy uzerinden cikan IP: $egressIp (dogrudan: $($direct.Ip)) -> trafik dugum uzerinden cikiyor."
    }
    # Ulke bilgisi
    foreach ($ip in $proxied) {
        try {
            $geo = Invoke-RestMethod -Uri "http://ip-api.com/json/$ip`?fields=status,country,countryCode" -TimeoutSec 10 -ErrorAction Stop
            if ($geo.status -eq "success") {
                Write-Info "  egress $ip -> $($geo.country) ($($geo.countryCode))"
            }
        } catch { }
    }
}

# ---------------------------------------------------------------------------
# 3. Core log dogrulamasi
# ---------------------------------------------------------------------------
Write-Step "3) Core log dogrulamasi"

$logDirs = @()
if ($resolvedDir) { $logDirs += (Join-Path $resolvedDir "guiLogs") }
$logDirs += (Join-Path $PSScriptRoot "guiLogs")

$accessLog = $null
$errorLog = $null
$foundLogDir = $null
foreach ($d in $logDirs) {
    if (-not (Test-Path $d)) { continue }
    $foundLogDir = $d
    $today = Get-Date -Format "yyyy-MM-dd"
    $va = Get-ChildItem $d -Filter "Vaccess_$today.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
    $ve = Get-ChildItem $d -Filter "Verror_$today.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
    $sa = Get-ChildItem $d -Filter "sbox_$today.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
    $se = Get-ChildItem $d -Filter "ao_singbox_$today.log" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($va) { $accessLog = $va.FullName; $errorLog = if ($ve) { $ve.FullName } else { $errorLog } }
    elseif ($sa) { $accessLog = $sa.FullName; $errorLog = if ($se) { $se.FullName } else { $errorLog } }
    if ($accessLog) { break }
}

if (-not $accessLog) {
    Write-Warn "Core log" "Access log bulunamadi (guiLogs\Vaccess_*.txt / sbox_*.txt). Uygulama ayarlarinda log kaydini acin (Ayarlar -> log)."
} else {
    # Test isteklerini loga yazdirmak icin bir kez daha proxy uzerinden istek at
    $before = (Get-Item $accessLog).Length
    & $curl -sS --max-time 20 -x "socks5h://127.0.0.1:$localPort" $Target | Out-Null

    Start-Sleep -Milliseconds 500
    $after = (Get-Item $accessLog).Length
    $newLines = @()
    if ($after -gt $before) {
        $fs = [System.IO.File]::Open($accessLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $fs.Seek($before, [System.IO.SeekOrigin]::Begin) | Out-Null
            $reader = [System.IO.StreamReader]::new($fs)
            while (-not $reader.EndOfStream) { $newLines += $reader.ReadLine() }
            $reader.Dispose()
        } finally { $fs.Dispose() }
    }
    $targetLines = @($newLines | Where-Object { $_ -like "*$targetHost*" })
    if ($targetLines.Count -gt 0) {
        Write-Pass "Core access log" "$targetHost icin $($targetLines.Count) kayit gorunuyor (log: $accessLog)"
        foreach ($l in $targetLines | Select-Object -Last 3) {
            Write-Info "    $l"
        }
    } else {
        # Hic satir gelmediyse tumuyle tara (dosya truncate olmus olabilir)
        $allLines = Get-Content $accessLog -ErrorAction SilentlyContinue
        $targetAll = @($allLines | Where-Object { $_ -like "*$targetHost*" })
        if ($targetAll.Count -gt 0) {
            Write-Pass "Core access log" "$targetHost icin toplam $($targetAll.Count) kayit (log: $accessLog)"
        } else {
            Write-Fail "Core access log" "Test istegi core access logunda gorunmuyor -> trafik bu cekirdekten gecmiyor veya log kapali (log: $accessLog)"
        }
    }
}

# Hata logu: REALITY / handshake / reddedilme ipuclari
if ($errorLog -and (Test-Path $errorLog)) {
    $err = Get-Content $errorLog -Tail 40 -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "REALITY|reality|received real certificate|handshake|rejected|refused|timeout|failed" }
    if ($err) {
        Write-Host "  [INFO] Hata logu ipuclari:" -ForegroundColor Magenta
        foreach ($l in $err | Select-Object -Last 6) { Write-Host "    $l" -ForegroundColor Magenta }
        if (($err -join " ") -match "received real certificate|REALITY") {
            Write-Host "  [HINT] REALITY el sikismasi reddediliyor: sunucu (3x-ui) istemcinin gomulu Xray versiyonunu kabul etmiyor olabilir. v2rayN'nin patch'li 26.6.1'i calisiyor ama gomulu 26.3.27 reddediliyor. Cekirdegi guncelleyin (Uygulama -> Cekirdekleri Guncelle) veya bir ust surum deneyin." -ForegroundColor Yellow
        }
    } else {
        Write-Info "Hata logunda REALITY/handshake hatasi yok (log: $errorLog)"
    }
}

# ---------------------------------------------------------------------------
# Ozet
# ---------------------------------------------------------------------------
Write-Step "Ozet"
$total = $script:passCount + $script:failCount + $script:warnCount
foreach ($r in $script:results) {
    $color = switch ($r.Result) { "PASS" { "Green" } "FAIL" { "Red" } default { "Yellow" } }
    Write-Host ("  [{0,-4}] {1}" -f $r.Result, $r.Check) -ForegroundColor $color
    if ($r.Detail) { Write-Host ("        " + $r.Detail) -ForegroundColor Gray }
}
Write-Host ""
Write-Host ("  PASS: {0}   FAIL: {1}   WARN: {2}" -f $script:passCount, $script:failCount, $script:warnCount) -ForegroundColor Cyan
if ($script:failCount -eq 0) {
    Write-Host "  SONUC: Proxy-only egress DOGRULANDI - sistem proxy'sine saygi duyan uygulamalar dugum uzerinden cikiyor." -ForegroundColor Green
} else {
    Write-Host "  SONUC: Dogrulama BASARISIZ - yukaridaki FAIL maddelerini inceleyin." -ForegroundColor Red
}
