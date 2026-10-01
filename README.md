# ClipSound

Ein schnelles Soundboard für Meme-Sounds: Klick oder Tastendruck spielt den Sound ab, die Lautstärke geht bis 500 %. Es gibt ClipSound für Mac, Windows, iPhone und als Webseite.

**Download:** [Releases](../../releases/latest)

| Ordner | Was | Bauen / Starten |
|---|---|---|
| `macos/` | Mac-App (SwiftUI, AVAudioEngine) | `./macos/build-app.sh` → `macos/dist/ClipSound.app` |
| `windows/` | Windows-App (WPF im Windows-11-Design, NAudio) | `dotnet publish windows/ClipSound -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o windows/dist` |
| `ios/` | iPhone- und iPad-App (SwiftUI) | `cd ios && xcodegen generate`, dann in Xcode öffnen |
| `server.js`, `public/` | Webseite auf `localhost:3000` | `node server.js` oder `./start.sh` |

## Tastenkürzel (Mac und Windows)

- Jeder Sound bekommt seine eigene Taste. Neue Sounds erhalten automatisch die nächste freie Taste aus 1–0 und Q–P.
- Zum Ändern: auf das Tasten-Feld der Kachel klicken oder Rechtsklick → **Taste festlegen …**, dann die Taste oder Kombination drücken. ⌫ bzw. Rücktaste entfernt die Taste.
- **Im Hintergrund** (z. B. in Spielen oder Discord) funktionieren Kombinationen
  - am Mac mit **⌃ (ctrl)**, z. B. ⌃⌥1,
  - unter Windows mit **Umschalt + Strg oder Alt**, z. B. Strg+Umschalt+1.

  Solche Kürzel haben in der App ein 🌐-Symbol.
- **Esc** stoppt alles.

## Allgemein

- Mit **Überlappen** laufen mehrere Sounds gleichzeitig.
- Die Apps starten leer. Sounds lassen sich einzeln oder als ganzer Ordner importieren, auch per Drag & Drop.
- Auf dem iPhone liegen die Sounds in der Dateien-App unter *Auf meinem iPhone › ClipSound*.

## Voraussetzungen

- Mac: macOS 15+, Xcode
- Windows: Windows 10/11 (64 Bit). Zum Bauen braucht man das .NET 10 SDK; der Build läuft auch auf macOS.
- iPhone: iOS 17+, Xcode und xcodegen
- Web: Node.js 18+
