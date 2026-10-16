#!/bin/zsh
# Baut Mac-, Windows- und Linux-App und veröffentlicht sie als GitHub-Release.
# Die eingebauten Updater finden das Update über die Dateinamen
# ClipSound-macOS.zip, ClipSound-Windows-x64.zip und ClipSound-Linux-x64/arm64.tar.gz – bitte nicht umbenennen.
#
#   ./release.sh notes.md     (Version vorher in VERSION erhöhen)
set -e
cd "$(dirname "$0")"
VERSION="$(cat VERSION)"
NOTES="${1:?Release-Notizen als Markdown-Datei angeben}"
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

./macos/build-app.sh
rm -rf windows/dist
dotnet publish windows/ClipSound -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o windows/dist
./linux/build.sh

OUT="$(mktemp -d)"
ditto -c -k --keepParent macos/dist/ClipSound.app "$OUT/ClipSound-macOS.zip"
ditto -c -k windows/dist/ClipSound.exe "$OUT/ClipSound-Windows-x64.zip"

git tag -f "v$VERSION"
git push -f origin "v$VERSION"
gh release create "v$VERSION" "$OUT/ClipSound-macOS.zip" "$OUT/ClipSound-Windows-x64.zip" \
  linux/dist/ClipSound-Linux-x64.tar.gz linux/dist/ClipSound-Linux-arm64.tar.gz \
  --title "ClipSound $VERSION" --notes-file "$NOTES" --latest
echo "Veröffentlicht: ClipSound $VERSION"
