#!/bin/bash

Arch="$1"
OutputPath="$2"
Version="$3"

FileName="AoGPN-${Arch}.zip"
wget -nv -O $FileName "https://github.com/AhmetOzbay27/AoGPN-core-bin/raw/refs/heads/master/$FileName"
7z x $FileName
cp -rf AoGPN-${Arch}/* $OutputPath

# Ensure the bundle actually carries the core runtimes before packaging.
if [[ ! -d "$OutputPath/bin/xray" || ! -d "$OutputPath/bin/sing_box" ]] || \
   ! ls "$OutputPath"/bin/xray/* >/dev/null 2>&1 || \
   ! ls "$OutputPath"/bin/sing_box/* >/dev/null 2>&1; then
  echo "[!] WARNING: bundle does not contain core binaries under bin/xray and bin/sing_box." >&2
  echo "[!] The package will be built without runnable cores." >&2
fi

PackagePath="AoGPN-Package-${Arch}"
mkdir -p "$PackagePath/AoGPN.app/Contents/Resources"
cp -rf "$OutputPath" "$PackagePath/AoGPN.app/Contents/MacOS"
cp -f "$PackagePath/AoGPN.app/Contents/MacOS/AoGPN.icns" "$PackagePath/AoGPN.app/Contents/Resources/AppIcon.icns"
echo "When this file exists, app will not store configs under this folder" > "$PackagePath/AoGPN.app/Contents/MacOS/NotStoreConfigHere.txt"
chmod +x "$PackagePath/AoGPN.app/Contents/MacOS/AoGPN"

cat >"$PackagePath/AoGPN.app/Contents/Info.plist" <<-EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleLocalizations</key>
  <array>
    <string>zh-Hans</string>
    <string>zh-Hant</string>
    <string>en</string>
    <string>fa</string>
    <string>fr</string>
    <string>ru</string>
    <string>hu</string>
  </array>
  <key>CFBundleDisplayName</key>
  <string>AoGPN</string>
  <key>CFBundleExecutable</key>
  <string>AoGPN</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIconName</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>AhmetOzbay27.AoGPN</string>
  <key>CFBundleName</key>
  <string>AoGPN</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${Version}</string>
  <key>CSResourcesFileMapped</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>13.7</string>
</dict>
</plist>
EOF

create-dmg \
    --volname "AoGPN Installer" \
    --window-size 700 420 \
    --icon-size 100 \
    --icon "AoGPN.app" 160 185 \
    --hide-extension "AoGPN.app" \
    --app-drop-link 500 185 \
    "AoGPN-${Arch}.dmg" \
    "$PackagePath/AoGPN.app"
