# ClipSound

Ein schnelles Soundboard für Meme-Sounds: Klick oder Tastendruck spielt den Sound ab, die Lautstärke geht bis 500 %. Es gibt ClipSound für Mac, Windows, Linux, iPhone und als Webseite.

**Download:** [Releases](../../releases/latest)

| Ordner | Was | Bauen / Starten |
|---|---|---|
| `macos/` | Mac-App (SwiftUI im Liquid-Glass-Design ab macOS 26, AVAudioEngine) | `./macos/build-app.sh` → `macos/dist/ClipSound.app` |
| `windows/` | Windows-App (WPF im Windows-11-Design, NAudio) | `dotnet publish windows/ClipSound -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o windows/dist` |
| `linux/` | Linux-App (GTK 4 + libadwaita wie GNOME-Apps, C#) | `./linux/build.sh` → `linux/dist/ClipSound-Linux-x64.tar.gz` und `-arm64.tar.gz` |
| `ios/` | iPhone- und iPad-App (SwiftUI) | `cd ios && xcodegen generate`, dann in Xcode öffnen |
| `server.js`, `public/` | Webseite auf `localhost:3000` | `node server.js` oder `./start.sh` |
| `cloud/` | Server für Lobbys, Konten und Clans (läuft auf Railway) | `cd cloud && npm ci && node server.js` (braucht `DATABASE_URL`) |

## Tastenkürzel (Mac, Windows, Linux)

- Jeder Sound bekommt seine eigene Taste. Neue Sounds erhalten automatisch die nächste freie Taste aus 1–0 und Q–P.
- Zum Ändern: auf das Tasten-Feld der Kachel klicken oder Rechtsklick → **Taste festlegen …**, dann die Taste oder Kombination drücken. ⌫ bzw. Rücktaste entfernt die Taste.
- **Im Hintergrund** (z. B. in Spielen oder Discord) funktionieren Kombinationen
  - am Mac mit **⌃ (ctrl)**, z. B. ⌃⌥1,
  - unter Windows mit **Umschalt + Strg oder Alt**, z. B. Strg+Umschalt+1,
  - unter Linux mit **Alt** oder **Strg+Umschalt**, z. B. Alt+2 – in einer X11-Sitzung. Unter Wayland erlaubt das System keine fremden globalen Kürzel; dort gehen die Tasten, solange das ClipSound-Fenster aktiv ist.

  Solche Kürzel haben in der App ein 🌐-Symbol.
- **Esc** stoppt alles.

## Allgemein

- Mit **Überlappen** laufen mehrere Sounds gleichzeitig.
- **Bilder** (Mac): Jeder Sound kann ein Bild bekommen – Rechtsklick → *Bild hinzufügen …*, *Bild aus der Zwischenablage* oder ein Bild direkt auf die Kachel ziehen. Die Bilder bleiben auf dem Mac (`~/Library/Application Support/ClipSound/Bilder`).
- Die Apps starten leer. Sounds lassen sich einzeln oder als ganzer Ordner importieren, auch per Drag & Drop.
- Auf dem iPhone liegen die Sounds in der Dateien-App unter *Auf meinem iPhone › ClipSound*.

## Lobbys und Clans

- In einer **Lobby** hört man zusammen: Wer einen Sound drückt, startet ihn bei allen gleichzeitig. Ein Konto braucht man dafür nicht.
- **Clans** funktionieren wie Discord-Server: Wer den Invite-Code oder den Link `…/c/CODE` hat, ist sofort drin. Admins können einen neuen Code erzeugen, Leute entfernen und Sounds löschen; der Besitzer kann den Clan auflösen.
- **Der Server speichert keine Audio-Dateien.** In der Datenbank stehen nur Listen (Dateiname, Größe, SHA-256). Braucht jemand einen Sound, bittet der Server ein Gerät, das ihn hat, und reicht die Datei im Arbeitsspeicher direkt weiter. Ein Clan-Sound ist also nur zu haben, solange jemand mit dem Sound ClipSound offen hat.
- Datenschutzerklärung: `/datenschutz` auf dem Server, in allen Apps verlinkt (Konto-Ansicht, Mac: Hilfe-Menü, Windows und Linux: Einstellungen, iPhone: Lobby-Blatt). Das Konto lässt sich in jeder App löschen.
- Testen: `node cloud/lobby-tool.mjs check <url>` (Lobby) und `DATABASE_URL=… node cloud/lobby-tool.mjs clan-check <url>` (Konten, Clans, Clan-Session).

## Updates

Mac-, Windows- und Linux-App suchen beim Start auf GitHub nach einer neueren Version. Wenn es eine gibt, erscheint oben eine Leiste mit **Jetzt aktualisieren**: Die App lädt das Update, ersetzt sich selbst und startet neu. Am Mac geht das auch über *ClipSound › Nach Updates suchen …*.

Ein neues Release veröffentlichen:

1. Die Versionsnummer in `VERSION` erhöhen.
2. `./release.sh notizen.md` ausführen.

## Voraussetzungen

- Mac: macOS 15+ (Liquid Glass ab macOS 26). Zum Bauen Xcode 26 oder neuer – `actool` baut daraus das Icon (`macos/Resources/AppIcon.icon`, Icon Composer)
- Windows: Windows 10/11 (64 Bit). Zum Bauen braucht man das .NET 10 SDK; der Build läuft auch auf macOS.
- Linux: GTK 4.14+ und libadwaita 1.5+ – also Ubuntu 24.04, Fedora 40, Debian 13, Linux Mint 22, Pop!_OS 24.04 oder neuer, Arch, openSUSE Tumbleweed. x64 und ARM64. Installieren: Archiv entpacken, `./install.sh` (ohne Root; App-Menü, Symbol, Befehl `clipsound`). FLAC, M4A, AAC und Opus brauchen `ffmpeg`. Ton über PulseAudio/PipeWire, sonst ALSA.
- iPhone: iOS 17+, Xcode und xcodegen
- Web: Node.js 18+
