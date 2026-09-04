<#
.SYNOPSIS
    Builds the AoGPN core-bundle zips (AoGPN-windows-64.zip / AoGPN-windows-arm64.zip)
    that the CI release pipeline (package-zip.yml) downloads from the
    AhmetOzbay27/AoGPN-core-bin repository.

.DESCRIPTION
    Assembles the same bin/ layout AoGPN expects at runtime:
        AoGPN-windows-64/           (top-level folder inside the zip)
          bin/
            geosite.dat geoip.dat Country.mmdb GeoLite2-ASN.mmdb
            xray/xray.exe
            sing_box/sing-box.exe
            mihomo/mihomo-windows-amd64-v1.exe     (x64)  or  mihomo-windows-arm64.exe (arm64)

    Core versions resolve to the latest stable GitHub release at run time,
    mirroring the app's own update logic (CoreInfoManager download patterns):
      XTLS/Xray-core     Xray-windows-64.zip / Xray-windows-arm64-v8a.zip
      SagerNet/sing-box  sing-box-<ver>-windows-amd64.zip / -windows-arm64.zip
      MetaCubeX/mihomo   mihomo-windows-amd64-v1-<ver>.zip / mihomo-windows-arm64-<ver>.zip

    The mihomo zip ships its exe as "mihomo.exe"; it is renamed to the
    variant name AoGPN's CoreExes list resolves (bin/mihomo/
    mihomo-windows-amd64-v1.exe on x64, mihomo-windows-arm64.exe on arm64).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\make-core-bundles.ps1

.EXAMPLE
    # Pin versions for reproducible bundles
    powershell -ExecutionPolicy Bypass -File .\make-core-bundles.ps1 `
        -XrayVersion v26.3.27 -SingBoxVersion v1.14.0 -MihomoVersion v1.19.30

.NOTES
    Requires an internet connection. Uses curl.exe when available (bundled
    with Windows 10+) and falls back to Invoke-WebRequest.
#>
[CmdletBinding()]
param(
    # Where the finished zips and scratch files go (gitignored as out-core-bundles/)
    [string]$OutDir = "out-core-bundles",

    # Optional version pins (defaults: latest stable release of each core)
    [string]$XrayVersion = "",
    [string]$SingBoxVersion = "",
    [string]$MihomoVersion = "",

    # Skip downloading the geo assets (geosite/geoip/mmdb); the app can fetch
    # them itself on first run
    [switch]$SkipGeo,

    # Keep downloaded archives and extracted trees for inspection
    [switch]$KeepDownloads
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$apiHeaders = @{ "User-Agent" = "AoGPN-core-bundle-builder" }
$geoFiles = @(
    "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
    "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
    "https://raw.githubusercontent.com/Loyalsoldier/geoip/release/Country.mmdb",
    "https://raw.githubusercontent.com/P3TERX/GeoLite.mmdb/download/GeoLite2-ASN.mmdb"
)

function Get-LatestTag([string]$repo) {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $apiHeaders
    return $rel.tag_name
}

function Resolve-Version([string]$name, [string]$pinned, [string]$repo) {
    if ($pinned) {
        Write-Host "[$name] pinned version: $pinned"
        return $pinned
    }
    $tag = Get-LatestTag $repo
    Write-Host "[$name] latest stable: $tag"
    return $tag
}

function Invoke-Download([string]$url, [string]$dest) {
    $destDir = Split-Path -Parent $dest
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -fL --retry 3 -o $dest $url
        if ($LASTEXITCODE -ne 0) { throw "curl failed for $url" }
    }
    else {
        Invoke-WebRequest -Uri $url -OutFile $dest
    }
}

function Expand-ArchiveFlat([string]$zipPath, [string]$destDir) {
    # AoGPN probes bin\<core>\<exe> directly (CoreBinaryRegistry /
    # CoreInfoManager), but the official release zips wrap the binaries in a
    # versioned top-level folder (sing-box-1.14.0-windows-amd64\…,
    # mihomo-windows-amd64-v1-v1.19.30\…) and Xray's are flat. Expand into a
    # scratch dir, then hoist the archive contents so the executables land flat
    # in $destDir — mirroring what the app's own update pipeline produces
    # (FileUtils.ZipExtractToFile extracts by entry, never preserving nesting).
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
    $scratch = Join-Path ([IO.Path]::GetDirectoryName($destDir)) ("_flat-" + [IO.Path]::GetFileNameWithoutExtension($zipPath))
    if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
    Expand-Archive -Path $zipPath -DestinationPath $scratch -Force
    # A single wrapping folder is hoisted; loose files (or several top-level
    # folders, should an archive ever change shape) are copied alongside so
    # nothing the archive shipped is lost.
    $children = Get-ChildItem -LiteralPath $scratch -Force
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $children = Get-ChildItem -LiteralPath $children[0].FullName -Force
    }
    foreach ($child in $children) {
        Copy-Item -LiteralPath $child.FullName -Destination $destDir -Recurse -Force
    }
    Remove-Item -Recurse -Force $scratch
}

function Build-Bundle([string]$arch, [string]$folderName, [string]$zipName,
                      [string]$xrayVer, [string]$singboxVer, [string]$mihomoVer) {
    Write-Host ""
    Write-Host "=== Building $zipName ($arch) ===" -ForegroundColor Cyan

    $work = Join-Path $OutDir ".work-$arch"
    $stage = Join-Path $work $folderName
    $bin = Join-Path $stage "bin"

    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force -Path (Join-Path $bin "xray")    | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $bin "sing_box") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $bin "mihomo")  | Out-Null

    # --- Xray -----------------------------------------------------------------
    $xrayAsset = if ($arch -eq "64") { "Xray-windows-64.zip" } else { "Xray-windows-arm64-v8a.zip" }
    $xrayUrl = "https://github.com/XTLS/Xray-core/releases/download/$xrayVer/$xrayAsset"
    $xrayZip = Join-Path $work $xrayAsset
    Write-Host "[xray] $xrayUrl"
    Invoke-Download $xrayUrl $xrayZip
    Expand-ArchiveFlat -ZipPath $xrayZip -DestDir (Join-Path $bin "xray")
    # The app skips the geo files bundled inside the xray zip ("geo" filter in
    # FileUtils.ZipExtractToFile) — geo data lives at bin\ root, so mirror that.
    Get-ChildItem (Join-Path $bin "xray") -File | Where-Object { $_.Name -like "*geo*.dat" } |
        Remove-Item -Force

    # --- sing-box --------------------------------------------------------------
    $winArch = if ($arch -eq "64") { "amd64" } else { "arm64" }
    $sbAsset = "sing-box-$($singboxVer.TrimStart('v'))-windows-$winArch.zip"
    $sbUrl = "https://github.com/SagerNet/sing-box/releases/download/$singboxVer/$sbAsset"
    $sbZip = Join-Path $work $sbAsset
    Write-Host "[sing-box] $sbUrl"
    Invoke-Download $sbUrl $sbZip
    Expand-ArchiveFlat -ZipPath $sbZip -DestDir (Join-Path $bin "sing_box")
    # The versioned wrapper folder is hoisted by Expand-ArchiveFlat; the exe must
    # sit directly under bin\sing_box for CoreInfoManager to find it.
    if (-not (Test-Path (Join-Path $bin "sing_box\sing-box.exe"))) { throw "sing-box.exe missing after flat extraction of $sbZip" }

    # --- mihomo ----------------------------------------------------------------
    $mhAsset = if ($arch -eq "64") { "mihomo-windows-amd64-v1-$mihomoVer.zip" } else { "mihomo-windows-arm64-$mihomoVer.zip" }
    $mhUrl = "https://github.com/MetaCubeX/mihomo/releases/download/$mihomoVer/$mhAsset"
    $mhZip = Join-Path $work $mhAsset
    Write-Host "[mihomo] $mhUrl"
    Invoke-Download $mhUrl $mhZip
    Expand-ArchiveFlat -ZipPath $mhZip -DestDir (Join-Path $bin "mihomo")
    # mihomo zips ship "mihomo.exe"; AoGPN resolves the variant name (search is
    # recursive so a renamed leftover in a stray subfolder can never shadow it)
    $mhExe = Get-ChildItem (Join-Path $bin "mihomo") -Recurse -File -Filter "*.exe" | Select-Object -First 1
    if (-not $mhExe) { throw "No .exe found in $mhZip" }
    $mhTarget = if ($arch -eq "64") { "mihomo-windows-amd64-v1.exe" } else { "mihomo-windows-arm64.exe" }
    if ($mhExe.Name -ne $mhTarget) {
        Rename-Item -Path $mhExe.FullName -NewName $mhTarget
        Write-Host "[mihomo] renamed $($mhExe.Name) -> $mhTarget"
    }
    # Keep only the executable (drop LICENSE/README noise), then make sure it
    # ended up directly under bin\mihomo where AoGPN resolves it.
    Get-ChildItem (Join-Path $bin "mihomo") -Recurse -File | Where-Object { $_.FullName -ne $mhExe.FullName -and $_.Name -ne $mhTarget } |
        Remove-Item -Force
    if ($mhExe.DirectoryName -ne (Join-Path $bin "mihomo")) {
        Move-Item -Path $mhExe.FullName -Destination (Join-Path $bin "mihomo\$mhTarget") -Force
        Write-Host "[mihomo] moved to $mhTarget root of bin\mihomo"
    }

    # --- geo assets (optional) ---------------------------------------------------
    if (-not $SkipGeo) {
        foreach ($url in $geoFiles) {
            $name = Split-Path -Leaf $url
            $dest = Join-Path $bin $name
            if (-not (Test-Path $dest)) {
                Write-Host "[geo] $name"
                Invoke-Download $url $dest
            }
        }
    }

    # --- package ----------------------------------------------------------------
    $zipPath = Join-Path $OutDir $zipName
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Write-Host "[zip] creating $zipPath (top folder: $folderName)"
    Compress-Archive -Path $stage -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host "--- contents of $zipName ---" -ForegroundColor Green
    Get-ChildItem $bin -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1)
        "{0,12:N0}  {1}" -f $_.Length, $rel
    }
    Get-FileHash $zipPath -Algorithm SHA256 | ForEach-Object {
        Write-Host "SHA256 ($zipName): $($_.Hash.ToLowerInvariant())" -ForegroundColor Green
    }

    if (-not $KeepDownloads) {
        Remove-Item -Recurse -Force $work
    }
}

# --- main --------------------------------------------------------------------
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

$vXray   = Resolve-Version "xray"     $XrayVersion   "XTLS/Xray-core"
$vSingbox = Resolve-Version "sing-box" $SingBoxVersion "SagerNet/sing-box"
$vMihomo = Resolve-Version "mihomo"   $MihomoVersion "MetaCubeX/mihomo"

Build-Bundle "64"    "AoGPN-windows-64"    "AoGPN-windows-64.zip"    $vXray $vSingbox $vMihomo
Build-Bundle "arm64" "AoGPN-windows-arm64" "AoGPN-windows-arm64.zip" $vXray $vSingbox $vMihomo

Write-Host ""
Write-Host "Done. Publish these two zips to the master branch of" -ForegroundColor Cyan
Write-Host "https://github.com/AhmetOzbay27/AoGPN-core-bin" -ForegroundColor Cyan
Write-Host "(package-zip.yml downloads them from raw.githubusercontent.com)." -ForegroundColor Cyan