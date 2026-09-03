<#
.SYNOPSIS
    AoGPN Logs klasörü bakım betisi: büyük günlükleri arşivler, eski logları temizler.

.DESCRIPTION
    Logs klasörüne tek tıkla bakım yapar:
      1) Varsayılan 10 MB (veya -ArchiveThresholdMB) üzeri günlük dosyalarını
         Logs\archive\ altına ZIP'ler ve orijinalini siler. Kullanımdaki
         (kilitli) dosyalar atlanır — uygulama/çekirdek çalışırken zarar verilmez.
      2) Varsayılan 30 günden (veya -KeepDays) eski günlük dosyalarını siler.
      3) Arşivdeki zip'leri varsayılan 90 günden (veya -ArchiveKeepDays) eskiyse siler.

    Logs klasörünü otomatik bulur (sırasıyla):
      - -LogsPath parametresi,
      - betiğin yanındaki Logs klasörü (betik exe'nin yanına kopyalanmışsa),
      - $PSScriptRoot altında log dosyası içeren Logs klasörleri (en güncel olanı),
      - %LocalAppData%\AoGPN\Logs.

.PARAMETER LogsPath
    Logs klasörü yolu. Verilmezse otomatik bulunur.

.PARAMETER ArchiveThresholdMB
    Arşiv eşiği (MB). Varsayılan: 10.

.PARAMETER KeepDays
    Bu değerden eski günlük dosyaları silinir. Varsayılan: 30.

.PARAMETER ArchiveKeepDays
    Arşivdeki zip'ler bu değerden eskiyse silinir. Varsayılan: 90.

.PARAMETER DryRun
    Gerçekten değişiklik yapmaz; yalnızca ne yapılacağını listeler.

.EXAMPLE
    .\cleanup-logs.ps1 -DryRun
    .\cleanup-logs.ps1 -LogsPath "D:\Programlar\VPN\AoGPN\AoGPN\AoGPN\bin\Debug\net10.0-windows10.0.19041.0\Logs"
#>

[CmdletBinding()]
param(
    [string]$LogsPath,
    [double]$ArchiveThresholdMB = 10,
    [int]$KeepDays = 30,
    [int]$ArchiveKeepDays = 90,
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'

# Türkçe karakterler konsolda doğru görünsün (çift tıklamayla çalıştırınca da).
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
}
catch { /* eski konsol — yok say */ }

# Logs klasörünü bulur. Bulunamazsa $null döner.
function Resolve-LogsPath {
    param([string]$Hint)

    if (-not [string]::IsNullOrWhiteSpace($Hint)) {
        if (Test-Path -LiteralPath $Hint -PathType Container) { return (Resolve-Path -LiteralPath $Hint).Path }
        Write-Warning "Belirtilen yol bulunamadı: $Hint"
    }

    # Betik exe'nin yanındaysa (veya yanında Logs varsa)
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $nearby = Join-Path $PSScriptRoot 'Logs'
        if (Test-Path -LiteralPath $nearby -PathType Container) { return $nearby }
    }

    # $PSScriptRoot altında log dosyası içeren Logs klasörlerini tara, en güncelini seç.
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        try {
            $candidates = Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Directory -Filter 'Logs' -ErrorAction SilentlyContinue |
                Where-Object {
                    Get-ChildItem -LiteralPath $_.FullName -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.Name -like 'ao_singbox_*.log' -or $_.Name -like 'gpn-session.log' -or $_.Name -match '^\d{4}-\d{2}-\d{2}\.txt$' }
                } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if ($candidates) { return $candidates.FullName }
        }
        catch { /* tarama isteğe bağlıdır */ }
    }

    $localApp = Join-Path $env:LOCALAPPDATA 'AoGPN\Logs'
    if (Test-Path -LiteralPath $localApp -PathType Container) { return $localApp }

    return $null
}

# Dosya başka bir işlem tarafından açık/kilitli mi? (evet ise $true)
function Test-FileLocked {
    param([string]$Path)
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'None')
        $stream.Close()
        $stream.Dispose()
        return $false
    }
    catch {
        return $true
    }
}

# Arşivlenecek/silinecek günlük dosyaları (Logs kökü, archive hariç)
function Get-LogFiles {
    param([string]$Dir)
    Get-ChildItem -LiteralPath $Dir -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '^(ao_singbox_|sbox_)\d{4}-\d{2}-\d{2}\.log$' -or
        $_.Name -match '^(Verror|Vaccess)_\d{4}-\d{2}-\d{2}\.txt$' -or
        $_.Name -match '^\d{4}-\d{2}-\d{2}\.txt$' -or
        $_.Name -in @('ao_diag.txt', 'ao_diag_config.json', 'gpn-session.log', 'vpn-session.log', 'gpn_resilience.log')
    }
}

function Format-Bytes {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N1} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

$logs = Resolve-LogsPath -Hint $LogsPath
if (-not $logs) {
    Write-Error "Logs klasörü bulunamadı. -LogsPath ile belirtin (ör: .\cleanup-logs.ps1 -LogsPath `"D:\...\Logs`")."
    exit 1
}

$archiveDir = Join-Path $logs 'archive'
$thresholdBytes = [long]($ArchiveThresholdMB * 1MB)
$now = Get-Date
$summaryArchived = 0
$summaryDeleted = 0
$summarySkipped = 0
$freedBytes = 0L

Write-Host ""
Write-Host "=== AoGPN Logs Bakımı ===" -ForegroundColor Cyan
Write-Host ("Logs klasörü : {0}" -f $logs)
Write-Host ("Arşiv eşiği  : {0} MB | Silme ölçütü: {1} gün | Arşiv ömrü: {2} gün" -f $ArchiveThresholdMB, $KeepDays, $ArchiveKeepDays)
if ($DryRun) { Write-Host "Mod          : DRY-RUN (değişiklik yapılmaz)" -ForegroundColor Yellow }
Write-Host ""

# ── 1) Büyük dosyaları arşivle ────────────────────────────────────────────
$bigFiles = Get-LogFiles -Dir $logs | Where-Object { $_.Length -gt $thresholdBytes }
if ($bigFiles) {
    Write-Host "Büyük günlükler arşivleniyor (> $ArchiveThresholdMB MB):" -ForegroundColor Yellow
    foreach ($f in $bigFiles) {
        $zipName = $f.Name + '.zip'
        $zipPath = Join-Path $archiveDir $zipName

        if (Test-Path -LiteralPath $zipPath) {
            Write-Host ("  [ATLA] zip zaten var: {0}" -f $zipName) -ForegroundColor DarkGray
            $summarySkipped++
            continue
        }
        if (Test-FileLocked -Path $f.FullName) {
            Write-Host ("  [ATLA] kilitli (kullanımda): {0}" -f $f.Name) -ForegroundColor DarkGray
            $summarySkipped++
            continue
        }

        if ($DryRun) {
            Write-Host ("  [ARŞİV (dry)] {0,-12} -> archive\{1}" -f (Format-Bytes $f.Length), $zipName) -ForegroundColor Green
            $summaryArchived++
            $freedBytes += $f.Length
            continue
        }

        try {
            New-Item -ItemType Directory -Path $archiveDir -Force -ErrorAction Stop | Out-Null
            Compress-Archive -LiteralPath $f.FullName -DestinationPath $zipPath -CompressionLevel Optimal -ErrorAction Stop
            # Arşiv başarılıysa orijinali sil (kilit kontrolü tekrar)
            if (Test-FileLocked -Path $f.FullName) {
                Write-Host ("  [UYARI] arşivlendi ama orijinal kilitli — silinemedi: {0}" -f $f.Name) -ForegroundColor Red
                $summarySkipped++
                continue
            }
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            $zipSize = (Get-Item -LiteralPath $zipPath).Length
            Write-Host ("  [ARŞİV] {0,-12} -> {1,-12} archive\{2}" -f (Format-Bytes $f.Length), (Format-Bytes $zipSize), $zipName) -ForegroundColor Green
            $summaryArchived++
            $freedBytes += [Math]::Max(0, $f.Length - $zipSize)
        }
        catch {
            Write-Host ("  [HATA] arşivlenemedi: {0} — {1}" -f $f.Name, $_.Exception.Message) -ForegroundColor Red
            $summarySkipped++
        }
    }
}
else {
    Write-Host "Arşivlenecek büyük dosya yok." -ForegroundColor DarkGray
}

# ── 2) Eski günlükleri sil ────────────────────────────────────────────────
$cutoff = $now.AddDays(-$KeepDays)
$oldFiles = Get-LogFiles -Dir $logs | Where-Object { $_.LastWriteTime -lt $cutoff }
if ($oldFiles) {
    Write-Host ""
    Write-Host ("$KeepDays günden eski günlükler siliniyor:" -f $KeepDays) -ForegroundColor Yellow
    foreach ($f in $oldFiles) {
        if (Test-FileLocked -Path $f.FullName) {
            Write-Host ("  [ATLA] kilitli (kullanımda): {0}" -f $f.Name) -ForegroundColor DarkGray
            $summarySkipped++
            continue
        }
        if ($DryRun) {
            Write-Host ("  [SİL (dry)] {0,-12} {1}" -f (Format-Bytes $f.Length), $f.Name) -ForegroundColor Green
            $summaryDeleted++
            $freedBytes += $f.Length
            continue
        }
        try {
            $size = $f.Length
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop
            Write-Host ("  [SİL] {0,-12} {1}" -f (Format-Bytes $size), $f.Name) -ForegroundColor Green
            $summaryDeleted++
            $freedBytes += $size
        }
        catch {
            Write-Host ("  [HATA] silinemedi: {0} — {1}" -f $f.Name, $_.Exception.Message) -ForegroundColor Red
            $summarySkipped++
        }
    }
}
else {
    Write-Host ""
    Write-Host "Silinecek eski günlük yok." -ForegroundColor DarkGray
}

# ── 3) Eski arşivleri kırp ────────────────────────────────────────────────
if (Test-Path -LiteralPath $archiveDir -PathType Container) {
    $archiveCutoff = $now.AddDays(-$ArchiveKeepDays)
    $oldZips = Get-ChildItem -LiteralPath $archiveDir -File -Filter '*.zip' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $archiveCutoff }
    if ($oldZips) {
        Write-Host ""
        Write-Host ("$ArchiveKeepDays günden eski arşivler kırpılıyor:" -f $ArchiveKeepDays) -ForegroundColor Yellow
        foreach ($z in $oldZips) {
            if ($DryRun) {
                Write-Host ("  [SİL (dry)] arşiv: {0}" -f $z.Name) -ForegroundColor Green
                $summaryDeleted++
                continue
            }
            try {
                Remove-Item -LiteralPath $z.FullName -Force -ErrorAction Stop
                Write-Host ("  [SİL] arşiv: {0}" -f $z.Name) -ForegroundColor Green
                $summaryDeleted++
            }
            catch {
                Write-Host ("  [HATA] arşiv silinemedi: {0}" -f $z.Name) -ForegroundColor Red
                $summarySkipped++
            }
        }
    }
}

Write-Host ""
Write-Host ("Özet: {0} dosya arşivlendi, {1} dosya silindi, {2} dosya atlandı. Kazanılan alan: {3}." -f `
    $summaryArchived, $summaryDeleted, $summarySkipped, (Format-Bytes $freedBytes)) -ForegroundColor Cyan
if ($DryRun) {
    Write-Host "Dry-run tamamlandı — hiçbir dosya değiştirilmedi." -ForegroundColor Yellow
}
