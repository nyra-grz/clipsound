#!/bin/sh
# ClipSound für den aktuellen Nutzer installieren – ohne Root, ohne .NET:
#   ./install.sh            installieren bzw. aktualisieren (App-Menü, Symbol, Befehl „clipsound“)
#   ./install.sh --remove   wieder entfernen (deine Sounds bleiben)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
DATA="${XDG_DATA_HOME:-${HOME:?}/.local/share}"
DEST="$DATA/clipsound"
APPS="$DATA/applications"
ICON="$DATA/icons/hicolor/512x512/apps"
BIN="${HOME:?}/.local/bin"

if [ "$1" = "--remove" ]; then
  rm -rf "${DEST:?}"
  rm -f "$APPS/at.timur.ClipSound.desktop" "$ICON/clipsound.png" "$BIN/clipsound"
  echo "ClipSound ist entfernt. Deine Sounds (Musik-Ordner › ClipSound) und Einstellungen (~/.config/ClipSound) sind noch da."
  exit 0
fi

# Braucht GTK 4 und libadwaita (bei GNOME, Ubuntu, Fedora, Mint … schon dabei)
missing=""
for lib in libgtk-4.so.1 libadwaita-1.so.0; do
  if ! (ldconfig -p 2>/dev/null | grep -q "$lib") && ! ls /usr/lib*/"$lib" /usr/lib/*/"$lib" >/dev/null 2>&1; then missing="$missing $lib"; fi
done
# Ohne SVG-Lader zeigt GTK statt der Symbole nur Platzhalter (auf Desktops ist er fast immer schon da)
if command -v dpkg >/dev/null 2>&1 && ! dpkg -s librsvg2-common >/dev/null 2>&1; then
  echo "Hinweis: Für die Symbole fehlt der SVG-Lader:  sudo apt install librsvg2-common"
fi
if [ -n "$missing" ]; then
  echo "Hinweis: Es fehlt noch:$missing"
  echo "  Ubuntu, Debian, Mint, Pop!_OS:  sudo apt install libadwaita-1-0"
  echo "  Fedora:                         sudo dnf install libadwaita"
  echo "  Arch, Manjaro, EndeavourOS:     sudo pacman -S libadwaita"
  echo "  openSUSE:                       sudo zypper install libadwaita-1-0"
fi

mkdir -p "$DEST" "$APPS" "$ICON" "$BIN"
cp "$HERE/ClipSound" "$DEST/ClipSound"
cp -R "$HERE/Assets" "$DEST/"
chmod +x "$DEST/ClipSound"
cp "$HERE/Assets/icons/hicolor/512x512/apps/clipsound.png" "$ICON/clipsound.png"
ln -sf "$DEST/ClipSound" "$BIN/clipsound"
sed "s|@EXEC@|$DEST/ClipSound|" "$HERE/at.timur.ClipSound.desktop" > "$APPS/at.timur.ClipSound.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$DATA/icons/hicolor" >/dev/null 2>&1 || true
echo "ClipSound ist installiert – du findest es im App-Menü. Im Terminal: clipsound"
echo "Updates installiert ClipSound selbst (oben erscheint dann ein Hinweis)."
