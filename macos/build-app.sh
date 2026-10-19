#!/bin/zsh
# Baut "ClipSound.app" nach ./dist
set -e
cd "$(dirname "$0")"
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
swift build -c release

# App-Icon (Icon Composer, Resources/AppIcon.icon) und Akzentfarbe (Resources/Assets.xcassets).
# actool baut daraus Assets.car (Liquid-Glass-Icon ab macOS 26) und AppIcon.icns für ältere Systeme.
mkdir -p .build/assets
# (absolute Pfade: actool arbeitet über einen Hintergrunddienst mit eigenem Arbeitsordner)
xcrun actool "$PWD/Resources/AppIcon.icon" "$PWD/Resources/Assets.xcassets" --compile "$PWD/.build/assets" \
  --platform macosx --minimum-deployment-target 15.0 \
  --app-icon AppIcon --accent-color AccentColor \
  --output-partial-info-plist "$PWD/.build/assets/partial.plist" >/dev/null

APP="dist/ClipSound.app"
VERSION="$(cat ../VERSION)"
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/ClipSound "$APP/Contents/MacOS/ClipSound"
cp .build/assets/Assets.car .build/assets/AppIcon.icns "$APP/Contents/Resources/"

# SwiftPM trägt als SDK-Version nur das Mindest-System (15.0) ein. Dann zeigt macOS 26+ die App im alten
# Kompatibilitätsmodus – ohne Liquid Glass. Deshalb die echte SDK-Version nachtragen.
xcrun vtool -set-build-version macos 15.0 "$(xcrun --sdk macosx --show-sdk-version)" -replace \
  -output "$APP/Contents/MacOS/ClipSound" "$APP/Contents/MacOS/ClipSound" 2>/dev/null

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>ClipSound</string>
  <key>CFBundleDisplayName</key><string>ClipSound</string>
  <key>CFBundleIdentifier</key><string>com.daniel.clipsound</string>
  <key>CFBundleDevelopmentRegion</key><string>de</string>
  <key>CFBundleLocalizations</key><array><string>de</string></array>
  <key>CFBundleExecutable</key><string>ClipSound</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleIconName</key><string>AppIcon</string>
  <key>NSAccentColorName</key><string>AccentColor</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.entertainment</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
codesign --force --deep -s - "$APP"
echo "Fertig: $APP"
