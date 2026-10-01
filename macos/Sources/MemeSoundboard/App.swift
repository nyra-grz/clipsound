import SwiftUI
import AppKit

// Startargumente (zum Testen):
//   --library <ordner>   anderen Sound-Ordner benutzen
//   --snapshot <png>     Fenster nach dem Start als Bild speichern und beenden
//   --import <pfad>      Datei/Ordner importieren, Ergebnis ausgeben und beenden
//   --selftest           ersten Sound mit 500 % abspielen, Status ausgeben und beenden
enum LaunchArgs {
    static let args = ProcessInfo.processInfo.arguments
    static func value(_ name: String) -> String? {
        guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
        return args[i + 1]
    }
    static var library: URL? { value("--library").map { URL(fileURLWithPath: $0, isDirectory: true) } }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var board: Board?
    private var keyMonitor: Any?

    func applicationDidFinishLaunching(_ notification: Notification) {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            // Dialoge (z. B. „Importieren“) bekommen ihre Tasten selbst
            guard let board = self?.board, !(event.window is NSPanel) else { return event }
            let typing = event.window?.firstResponder is NSText
            return board.handleKey(event, typingInSearch: typing) ? nil : event
        }

        if let path = LaunchArgs.value("--snapshot") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { Self.snapshot(to: path); NSApp.terminate(nil) }
        }
        if let path = LaunchArgs.value("--import") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                let r = self.board?.library.importItems([URL(fileURLWithPath: path)])
                print("IMPORT: added=\(r?.added ?? -1) rejected=\(r?.rejected ?? [])")
                fflush(stdout); exit(0)
            }
        }
        if LaunchArgs.args.contains("--selftest") {
            // warten, bis das Fenster steht und der Delegate das Board kennt
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.runSelftest() }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    /// Ganzes Fenster inkl. Titelleiste und Toolbar als PNG
    private static func snapshot(to path: String) {
        guard let window = NSApp.windows.first(where: { $0.isVisible }),
              let frameView = window.contentView?.superview,
              let rep = frameView.bitmapImageRepForCachingDisplay(in: frameView.bounds) else { return }
        frameView.cacheDisplay(in: frameView.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }

    private func runSelftest() {
        guard let board, let sound = board.library.sounds.first else { print("SELFTEST: keine Sounds"); exit(1) }
        let oldVolume = board.player.volume
        board.player.volume = 5
        let ok = board.player.play(sound)
        print("SELFTEST: play(\(sound.id)) = \(ok)")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            print("SELFTEST: progress = \(board.player.progress)")
            board.player.stopAll()
            print("SELFTEST: nach Stop = \(board.player.progress)")
            board.player.volume = oldVolume
            fflush(stdout)
            exit(0)
        }
    }
}

@main
struct MemeSoundboardApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var board: Board

    init() {
        let board = Board(library: SoundLibrary(folder: LaunchArgs.library))
        _board = StateObject(wrappedValue: board)
    }

    var body: some Scene {
        Window("Meme Soundboard", id: "main") {
            ContentView(board: board)
                .frame(minWidth: 620, minHeight: 380)
                .onAppear { delegate.board = board }
        }
        .defaultSize(width: 980, height: 640)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Sounds importieren …") { board.openImportPanel() }
                    .keyboardShortcut("o")
                Button("Sound-Ordner im Finder zeigen") { board.library.revealInFinder() }
                    .keyboardShortcut("o", modifiers: [.command, .shift])
            }
            CommandGroup(after: .textEditing) {
                Button("Suchen") { board.searchFocused = true }
                    .keyboardShortcut("f")
            }
            CommandMenu("Wiedergabe") {
                PlaybackCommands(player: board.player)
            }
        }
    }
}

/// Menü „Wiedergabe“ – eigene View, damit Häkchen und Zustand live aktualisiert werden
struct PlaybackCommands: View {
    @ObservedObject var player: SoundPlayer

    var body: some View {
        Button("Alles stoppen") { player.stopAll() }
            .keyboardShortcut(".")
        Toggle("Sounds überlappen", isOn: $player.overlap)
            .keyboardShortcut("l")
        Divider()
        Button("Lauter") { player.volume = min(SoundPlayer.maxVolume, player.volume + 0.25) }
            .keyboardShortcut(.upArrow)
        Button("Leiser") { player.volume = max(0, player.volume - 0.25) }
            .keyboardShortcut(.downArrow)
        Button("Lautstärke auf 100 %") { player.volume = 1 }
            .keyboardShortcut("0")
        Divider()
        Text("1–0 und Q–P spielen die ersten 20 Sounds")
    }
}
