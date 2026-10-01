#!/bin/zsh
# Baut "Meme Soundboard.app" nach ./dist
set -e
cd "$(dirname "$0")"
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
swift build -c release

# Icon erzeugen, falls noch nicht vorhanden
if [ ! -f .build/icon/AppIcon.icns ]; then
  mkdir -p .build/icon/AppIcon.iconset
  swift tools/make-icon.swift .build/icon/icon1024.png
  for s in 16 32 128 256 512; do
    sips -z $s $s .build/icon/icon1024.png --out .build/icon/AppIcon.iconset/icon_${s}x${s}.png >/dev/null
    sips -z $((s*2)) $((s*2)) .build/icon/icon1024.png --out .build/icon/AppIcon.iconset/icon_${s}x${s}@2x.png >/dev/null
  done
  iconutil -c icns .build/icon/AppIcon.iconset -o .build/icon/AppIcon.icns
fi
APP="dist/Meme Soundboard.app"
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/MemeSoundboard "$APP/Contents/MacOS/MemeSoundboard"
cp .build/icon/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Meme Soundboard</string>
  <key>CFBundleDisplayName</key><string>Meme Soundboard</string>
  <key>CFBundleIdentifier</key><string>com.daniel.memesoundboard</string>
  <key>CFBundleExecutable</key><string>MemeSoundboard</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.entertainment</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
codesign --force --deep -s - "$APP"
echo "Fertig: $APP"
