<#
.SYNOPSIS
    Resmi WireGuard istemcisindeki (WireGuard for Windows) kayip/bozuk tunel
    yapilandirmasini onarir ve tuneli yeniden kurar.

    Belirti: tunel Activate edilirken "The system cannot find the file specified"
    (Windows hata 2). Neden: tunelin DPAPI sifreli yapilandirma dosyasi
    (%ProgramFiles%\WireGuard\Data\Configurations\<ad>.conf.dpapi) silinmis/bozuk,
    veya tunel adi istemcinin kuralina uymuyor (Unicode "I talya" gibi -
    yalnizca ^[a-zA-Z0-9_=+.-]{1,32}$ gecerli, upstream conf/name.go).

.DESCRIPTION
    Yapilanlar (sira):
      1. Dogrulama: yonetici, wireguard.exe, conf dosyasi ([Interface]/[Peer] +
         PrivateKey), tunel adi kurali.
      2. Varsa WireGuardTunnel$<ad> hizmetini durdurur/siler.
      3. Configurations altindaki <ad>.conf.dpapi ve <ad>.conf dosyasini siler
         (bozuk hedef) + kurala uymayan Unicode kalinti dosyalarini temizler.
      4. Conf'u $TunnelName.conf adiyla gecici dizine kopyalar ve
         wireguard.exe /installtunnelservice ile yeniden kurar (ad, DOSYA
         ADINDAN gelir - Windows istemcisi "# Name =" satirini okumaz).
      5. Yeni <ad>.conf.dpapi dosyasini dogrular ve sonraki adimlari soyler.

    -DryRun ile hicbir degisiklik yapilmaz, yalnizca yapilacaklar listelenir.

.PARAMETER ConfPath
    Gecerli (anahtarli) WireGuard .conf dosyasi.
    Varsayilan: .freebuff\aogpn-client-it.conf.

.PARAMETER TunnelName
    Olusturulacak tunel adi. Yalnizca ASCII: [a-zA-Z0-9_=+.-], 1-32 karakter.
    Varsayilan: Italya  (Unicode "I talya" GECERSIZDIR - istemci reddeder).

.PARAMETER WireGuardDir
    WireGuard kurulum dizini. Bos ise $env:ProgramFiles\WireGuard varsayilir.

.PARAMETER DryRun
    Sadece raporlar, hicbir dosya/hizmet degismez.

.EXAMPLE
    # Italya.conf adiyla kaydedilmis conf'u kullanarak "Italya" tunelini onar
    powershell -ExecutionPolicy Bypass -File .\repair-wireguard-client-tunnel.ps1 `
        -ConfPath .\.freebuff\aogpn-client-it.conf -TunnelName Italya
#>
param(
    [string]$ConfPath = (Join-Path $PSScriptRoot ".freebuff\aogpn-client-it.conf"),
    [string]$TunnelName = "Italya",
    [string]$WireGuardDir = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }
function Write-Info([string]$msg)   { Write-Host "  $msg" -ForegroundColor Gray }
function Write-OK([string]$msg)     { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Warn([string]$msg)   { Write-Host "  [UYARI] $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg)   { Write-Host "  [HATA] $msg" -ForegroundColor Red }

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " WireGuard for Windows - Tunel Onarim (kayip/bozuk conf)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 1. Dogrulamalar
# ---------------------------------------------------------------------------
Write-Step "1) On kosullar"

# 1a. Yonetici (hizmet + Program Files yazimi gerekir)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent())
    .IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Fail "Yonetici yetkisi gerekli - PowerShell'i 'Yonetici olarak calistir' ile acin."
    exit 1
}
Write-OK "Yonetici yetkisi"

# 1b. Tunel adi kurali (upstream conf/name.go ile ayni)
if ($TunnelName -notmatch "^[a-zA-Z0-9_=+.-]{1,32}$") {
    Write-Fail "Tunel adi gecersiz: '$TunnelName' - yalnizca ASCII [a-zA-Z0-9_=+.-], 1-32 karakter olabilir (Unicode 'I talya' reddedilir)."
    exit 1
}
Write-OK "Tunel adi kurala uygun: $TunnelName"

# 1c. WireGuard kurulumu
if (-not $WireGuardDir) { $WireGuardDir = Join-Path $env:ProgramFiles "WireGuard" }
$wgExe = Join-Path $WireGuardDir "wireguard.exe"
if (-not (Test-Path $wgExe)) {
    Write-Fail "wireguard.exe bulunamadi: $wgExe - WireGuard for Windows kurulu degil mi?"
    exit 1
}
$confDir = Join-Path $WireGuardDir "Data\Configurations"
Write-OK "WireGuard: $wgExe"
Write-Info "Yapilandirma dizini: $confDir"

# 1d. Conf dosyasi
$confPathFull = (Resolve-Path $ConfPath -ErrorAction SilentlyContinue).Path
if (-not $confPathFull -or -not (Test-Path $confPathFull)) {
    Write-Fail "Conf dosyasi bulunamadi: $ConfPath"
    exit 1
}
$confText = Get-Content $confPathFull -Raw -Encoding UTF8
if ($confText -notmatch "(?m)^\s*\[Interface\]\s*$" -or
    $confText -notmatch "(?m)^\s*PrivateKey\s*=\s*\S+\s*$" -or
    $confText -notmatch "(?m)^\s*\[Peer\]\s*$") {
    Write-Fail "Conf gecersiz: [Interface] + PrivateKey + [Peer] bloklari bekleniyordu ($confPathFull)."
    exit 1
}
Write-OK "Conf gecerli: $confPathFull"

# ---------------------------------------------------------------------------
# 2. Bozuk tuneli kaldir (hizmet + config dosyalari)
# ---------------------------------------------------------------------------
Write-Step "2) Bozuk tunelin kaldirilmasi"

$serviceName = "WireGuardTunnel$" + $TunnelName

# 2a. Hizmet
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($DryRun) {
        Write-Warn "DryRun: '$serviceName' hizmeti durdurulup silinecek (su an: $($svc.Status))."
    } else {
        Write-Info "'$serviceName' hizmeti bulundu (durum: $($svc.Status)) - durduruluyor ve siliniyor..."
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $serviceName | Out-Null
        Start-Sleep -Milliseconds 500
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            Write-Fail "'$serviceName' silinemedi - elle inceleyin: sc.exe query $serviceName"
        } else {
            Write-OK "'$serviceName' hizmeti silindi"
        }
    }
} else {
    Write-Info "'$serviceName' hizmeti yok - atlandi"
}

# 2b. Hedef tunelin config dosyalari
foreach ($suffix in @(".conf.dpapi", ".conf")) {
    $target = Join-Path $confDir ($TunnelName + $suffix)
    if (Test-Path $target) {
        if ($DryRun) {
            Write-Warn "DryRun: '$target' silinecek (bozuk/eskimis yapilandirma)."
        } else {
            Remove-Item $target -Force
            Write-OK "Silindi: $target"
        }
    }
}

# 2c. Kurala uymayan Unicode kalinti dosyalari (orn. Italya.conf.dpapi)
if (Test-Path $confDir) {
    $legacy = @(Get-ChildItem $confDir -File | Where-Object {
        $_.Name -match "(?i)\.conf(\.dpapi)?$" -and
        ($_.Name -replace "(?i)\.conf(\.dpapi)?$", "") -notmatch "^[a-zA-Z0-9_=+.-]{1,32}$"
    })
    if ($legacy.Count -gt 0) {
        Write-Warn "Kurala uymayan (Unicode) kalinti yapilandirmalar bulundu - hicbir istemci surumunde icerilemez:"
        foreach ($f in $legacy) { Write-Warn "  - $($f.Name) ($($f.Length) bayt)" }
        if (-not $DryRun) {
            foreach ($f in $legacy) {
                Remove-Item $f.FullName -Force
                Write-OK "Silindi: $($f.FullName)"
            }
        } else {
            Write-Warn "DryRun: yukaridaki $($legacy.Count) dosya silinecek."
        }
    } else {
        Write-Info "Unicode kalinti dosya yok"
    }
}

# ---------------------------------------------------------------------------
# 3. Yeniden kur (ad DOSYA ADINDAN gelir -> $TunnelName.conf olarak kopyala)
# ---------------------------------------------------------------------------
Write-Step "3) Tunelin yeniden kurulmasi"

$tempConf = Join-Path $env:TEMP ($TunnelName + ".conf")
if ($DryRun) {
    Write-Warn "DryRun: '$confPathFull' -> '$tempConf' kopyalanacak ve kurulacak:"
    Write-Info "        & '$wgExe' /installtunnelservice '$tempConf'"
} else {
    Copy-Item $confPathFull $tempConf -Force
    Write-Info "Kuruluyor: wireguard.exe /installtunnelservice '$tempConf'"
    & $wgExe /installtunnelservice $tempConf
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Kurulum basarisiz (exit $LASTEXITCODE) - conf icerigini ve WireGuard loglarini inceleyin."
        exit 1
    }
    Remove-Item $tempConf -Force -ErrorAction SilentlyContinue

    # 4. Dogrulama
    Write-Step "4) Dogrulama"
    $newFile = Join-Path $confDir ($TunnelName + ".conf.dpapi")
    if (Test-Path $newFile) {
        $len = (Get-Item $newFile).Length
        if ($len -gt 0) {
            Write-OK "Yapilandirma olusturuldu: $newFile ($len bayt)"
            Write-Host ""
            Write-Host "  SONUC: Tunel '$TunnelName' kuruldu. WireGuard uygulamasini acin," -ForegroundColor Green
            Write-Host "         tuneli Activate edip 'ping 10.66.66.1' ile dogrulayin." -ForegroundColor Green
        } else {
            Write-Fail "Yapilandirma dosyasi olusturulamadi (0 bayt): $newFile"
            exit 1
        }
    } else {
        Write-Fail "Yapilandirma dosyasi bulunamadi: $newFile"
        exit 1
    }
}