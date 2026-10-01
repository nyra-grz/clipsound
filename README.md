# Meme Soundboard

Ein Soundboard für Meme-Sounds in drei Versionen. Jede Version spielt mit einem Klick oder Tastendruck ab und kann bis 500 % verstärken.

| Ordner | Was | Starten / Bauen |
|---|---|---|
| `server.js`, `public/` | Webseite auf `localhost:3000`, Sounds hochladen per Drag & Drop | `node server.js` oder `./start.sh` (öffnet Safari) |
| `macos/` | Native Mac-App (SwiftUI, AVAudioEngine) | `./macos/build-app.sh` → `macos/dist/Meme Soundboard.app` |
| `windows/` | Windows-App (WPF, NAudio) | `dotnet publish windows/MemeSoundboard -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o windows/dist` |

## Bedienung

- Tasten **1–0** und **Q–P** spielen die ersten 20 Sounds
- **Esc** stoppt alles
- **Überlappen** lässt mehrere Sounds gleichzeitig laufen
- Lautstärke von 0 bis 500 %

Die Apps starten leer. Sounds kommen per Import, als einzelne Dateien oder als ganzer Ordner, oder per Drag & Drop ins Fenster.

## Voraussetzungen

- Web: Node.js 18+
- Mac: macOS 15+, Xcode
- Windows-Build: .NET 8 SDK (der Build läuft auch auf macOS)
