import SwiftUI
import AppKit
import Combine

// Startargumente (zum Testen):
//   --library <ordner>   anderen Sound-Ordner benutzen
//   --snapshot <png>     Fenster nach dem Start als Bild speichern und beenden
//   --import <pfad>      Datei/Ordner importieren, Ergebnis ausgeben und beenden
//   --keytest            Tastenkürzel prüfen (⌃⌥K auf den ersten Sound), Ergebnis ausgeben und beenden
//   --recorder           Aufnahme-Fenster für den ersten Sound öffnen (für Screenshots)
//   --pretend-version <v> so tun, als wäre Version <v> installiert (Updater testen)
//   --update-now         verfügbares Update ohne Nachfrage installieren
//   --selftest           ersten Sound mit 500 % abspielen, Status ausgeben und beenden
//   --lobby-host         Lobby mit den eigenen Sounds öffnen, Code und Ereignisse ausgeben
//   --lobby-join <code>  Lobby beitreten, Ereignisse ausgeben
//   --lobby-accept       Anfragen/Angebote automatisch annehmen
//   --lobby-play-first   als Gast den ersten Sound drücken und danach behalten wollen
//   --lobby-exit-after <s> nach s Sekunden beenden
//   Server ändern: Umgebungsvariable CLIPSOUND_SERVER
enum LaunchArgs {
    static let args = ProcessInfo.processInfo.arguments
    static func value(_ name: String) -> String? {
        guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
        return args[i + 1]
    }
    static var library: URL? { value("--library").map { URL(fileURLWithPath: $0, isDirectory: true) } }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// wird schon in ClipSoundApp.init gesetzt – onAppear kommt manchmal erst spät
    static var sharedBoard: Board?
    var board: Board? {
        get { storedBoard ?? Self.sharedBoard }
        set { storedBoard = newValue }
    }
    private var storedBoard: Board?
    private var keyMonitor: Any?

    func applicationDidFinishLaunching(_ notification: Notification) {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            // Dialoge (z. B. „Importieren“) bekommen ihre Tasten selbst
            guard let board = self?.board, !(event.window is NSPanel) else { return event }
            let typing = event.window?.firstResponder is NSText
            return board.handleKey(event, typingInSearch: typing) ? nil : event
        }

        if let path = LaunchArgs.value("--snapshot") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { Self.snapshot(to: path); NSApp.terminate(nil) }
        }
        if let path = LaunchArgs.value("--import") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                let r = self.board?.library.importItems([URL(fileURLWithPath: path)])
                print("IMPORT: added=\(r?.added ?? -1) rejected=\(r?.rejected ?? [])")
                fflush(stdout); exit(0)
            }
        }
        if LaunchArgs.args.contains("--keytest") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.runKeytest() }
        }
        if LaunchArgs.args.contains("--recorder") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                if let board = self.board, let first = board.library.sounds.first { board.startRecording(first) }
            }
        }
        if LaunchArgs.args.contains("--lobby-host") || LaunchArgs.value("--lobby-join") != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.runLobbyTest() }
        }
        if let secs = LaunchArgs.value("--lobby-exit-after").flatMap(Double.init) {
            DispatchQueue.main.asyncAfter(deadline: .now() + secs) {
                print("LOBBY: ende bibliothek=\(self.board?.library.sounds.count ?? -1)"); fflush(stdout); exit(0)
            }
        }
        if LaunchArgs.args.contains("--selftest") {
            // warten, bis das Fenster steht und der Delegate das Board kennt
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.runSelftest() }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        board?.lobby.leave() // Lobby sauber verlassen, fremde Sounds aus dem Cache räumen
    }

    /// Ganzes Fenster inkl. Titelleiste und Toolbar als PNG
    private static func snapshot(to path: String) {
        guard let window = NSApp.windows.first(where: { $0.isVisible && !($0 is NSPanel) }) else {
            print("SNAPSHOT: kein Fenster – \(NSApp.windows.map { "\(type(of: $0)) visible=\($0.isVisible)" })"); return
        }
        guard let frameView = window.contentView?.superview,
              let rep = frameView.bitmapImageRepForCachingDisplay(in: frameView.bounds) else { print("SNAPSHOT: kein Bild"); return }
        frameView.cacheDisplay(in: frameView.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }

    private func runKeytest() {
        guard let board, let sound = board.library.sounds.first else { print("KEYTEST: keine Sounds"); exit(1) }
        func status() -> String {
            let binds = board.keys.binds
            let shown = board.library.sounds.prefix(3).map { "\($0.title)=\(binds[$0.id]?.display ?? "–")" }
            return "binds=\(binds.count) erste: \(shown.joined(separator: ", ")) global=\(board.hotKeyState(for: sound))"
        }
        print("KEYTEST vorher: \(status())")
        // ⌃⌥K: keyCode 40
        board.keys.set(KeyBind(keyCode: 40, flags: [.control, .option]), for: sound.id)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            print("KEYTEST nachher: \(status())")
            fflush(stdout)
            exit(0)
        }
    }

    private var lobbyWatch: AnyCancellable?
    private var lastLobbyState = ""
    private var playedFirst = false

    private func runLobbyTest() {
        // warten, bis das Fenster steht und der Delegate das Board kennt
        guard let board, NSApp.windows.contains(where: { $0.isVisible }) else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.runLobbyTest() }
            return
        }
        let lobby = board.lobby
        func out(_ s: String) { print("LOBBY: \(s)"); fflush(stdout) }
        let play = lobby.onPlay
        lobby.onPlay = { sound, delay in out("play \(sound.url.lastPathComponent) in \(Int(delay * 1000)) ms"); play?(sound, delay) }
        let receive = lobby.onReceive
        lobby.onReceive = { url in out("bekommen \(url.lastPathComponent)"); receive?(url) }
        lobbyWatch = lobby.objectWillChange.receive(on: DispatchQueue.main).sink { [weak self] _ in
            DispatchQueue.main.async {
                guard let self else { return }
                let ready = lobby.sounds.filter { $0.local != nil }.count
                let state = "phase=\(lobby.phase) code=\(lobby.code) host=\(lobby.isHost) sounds=\(ready)/\(lobby.sounds.count) " +
                    "hochgeladen=\(lobby.uploaded.map { "\($0.done)/\($0.total)" } ?? "-") leute=\(lobby.members.map(\.name)) " +
                    "note=\(lobby.note ?? "-") problem=\(lobby.problem ?? "-")"
                if state != self.lastLobbyState { self.lastLobbyState = state; out(state) }
                if LaunchArgs.args.contains("--lobby-accept"), let r = lobby.requests.first {
                    out("anfrage \(r.kind) \(r.soundName) von \(r.by) → ja")
                    lobby.answer(r, ok: true)
                }
                if LaunchArgs.args.contains("--lobby-play-first"), !self.playedFirst, lobby.phase == .open,
                   !lobby.sounds.isEmpty, ready == lobby.sounds.count, let first = lobby.sounds.first {
                    self.playedFirst = true
                    board.play(first)
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1) { lobby.ask(first) }
                }
            }
        }
        if let code = LaunchArgs.value("--lobby-join") { lobby.join(code) } else { lobby.open(with: board.library.sounds) }
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
            UserDefaults.standard.synchronize() // sonst bleibt 500 % gespeichert
            fflush(stdout)
            exit(0)
        }
    }
}

@main
struct ClipSoundApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var board: Board
    @StateObject private var updater = Updater()

    init() {
        let board = Board(library: SoundLibrary(folder: LaunchArgs.library))
        AppDelegate.sharedBoard = board
        _board = StateObject(wrappedValue: board)
    }

    var body: some Scene {
        Window("ClipSound", id: "main") {
            ContentView(board: board, updater: updater)
                .frame(minWidth: 620, minHeight: 380)
                .onAppear { delegate.board = board }
                .task {
                    // Beim Start nach Updates schauen (nicht bei Testläufen)
                    let testRun = ["--snapshot", "--selftest", "--keytest", "--import"].contains(where: LaunchArgs.args.contains)
                    guard !testRun || LaunchArgs.args.contains("--pretend-version") else { return }
                    await updater.check()
                    if LaunchArgs.args.contains("--update-now"), updater.available != nil { await updater.install() }
                }
        }
        .defaultSize(width: 980, height: 640)
        .commands {
            CommandGroup(after: .appInfo) {
                Button("Nach Updates suchen …") { Task { await updater.check(userInitiated: true) } }
            }
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
                PlaybackCommands(board: board, player: board.player)
            }
        }
    }
}

/// Menü „Wiedergabe“ – eigene View, damit Häkchen und Zustand live aktualisiert werden
struct PlaybackCommands: View {
    let board: Board
    @ObservedObject var player: SoundPlayer

    var body: some View {
        Button("Alles stoppen") { board.stopAll() }
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
        Text("Tasten: Rechtsklick auf einen Sound → Taste festlegen")
    }
}
