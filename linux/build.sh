#!/bin/bash
# Baut ClipSound für Linux (x64 und arm64) nach linux/dist/ClipSound-Linux-<arch>.tar.gz
# Läuft auch auf macOS. Danach liegt in jedem Archiv ein Ordner „ClipSound“ mit Programm, Symbolen und install.sh.
set -e
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="${DOTNET_ROOT:-$HOME/.dotnet}:$PATH" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p linux/dist
for arch in ${ARCHS:-x64 arm64}; do
  work="linux/dist/$arch"
  rm -rf "${work:?}"
  dotnet publish linux/ClipSound -c Release -r "linux-$arch" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -o "$work/ClipSound"
  cp linux/packaging/install.sh linux/packaging/at.timur.ClipSound.desktop linux/packaging/LIESMICH.txt "$work/ClipSound/"
  chmod +x "$work/ClipSound/install.sh" "$work/ClipSound/ClipSound"
  # ohne macOS-Zusatzdaten im Archiv
  COPYFILE_DISABLE=1 tar $( [ "$(uname)" = Darwin ] && echo --no-mac-metadata ) -C "$work" -czf "linux/dist/ClipSound-Linux-$arch.tar.gz" ClipSound
  echo "fertig: linux/dist/ClipSound-Linux-$arch.tar.gz"
done
