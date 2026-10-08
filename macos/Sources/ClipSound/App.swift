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
//   --mixer-demo         drei Sounds gleichzeitig starten, einen auf 160 %, einen ausblenden, den ersten zurückdrehen (für Screenshots)
//   --open-settings      Einstellungen gleich öffnen
//   --close-test         Hauptfenster nach 1 s wie mit dem roten Knopf schließen, Zustand ausgeben
//   --icon <bild>        eigenes App-Icon setzen (wie in den Einstellungen)
//   --selftest           ersten Sound mit 500 % abspielen, Status ausgeben und beenden
//   --lobby-host         Lobby mit den eigenen Sounds öffnen, Code und Ereignisse ausgeben
//   --lobby-join <code>  Lobby beitreten, Ereignisse ausgeben
//   --lobby-accept       Anfragen/Angebote automatisch annehmen
//   --lobby-play-first   als Gast den ersten Sound drücken und danach behalten wollen
//   --lobby-exit-after <s> nach s Sekunden beenden
//   --lobby-volumes      die versteckten Lautstärke-Regler gleich öffnen
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
        watchMainWindow()
        if LaunchArgs.args.contains("--close-test") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                NSApp.windows.first { $0.identifier?.rawValue == "main" }?.performClose(nil)
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    print("CLOSE: läuft noch, dock=\(NSApp.activationPolicy() == .regular) fenster=\(NSApp.windows.filter { $0.isVisible && $0.identifier?.rawValue == "main" }.count)"); fflush(stdout)
                }
            }
        }
        CustomIcon.shared.apply()
        if let path = LaunchArgs.value("--icon") { CustomIcon.shared.set(from: URL(fileURLWithPath: path)) }
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
        if LaunchArgs.args.contains("--mixer-demo") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) {
                guard let player = self.board?.player, let sounds = self.board?.library.sounds else { return }
                let overlap = player.overlap
                player.overlap = true
                for sound in sounds.prefix(3) { _ = player.play(sound) }
                player.overlap = overlap
                if player.channels.count > 1 {
                    // nur für das Bild – danach wieder auf den gemerkten Wert
                    let soundID = player.channels[1].soundID, old = player.level(of: soundID)
                    player.setLevel(1.6, for: soundID)
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2) { player.setLevel(old, for: soundID) }
                }
                if player.channels.count > 2 { player.fadeOut(player.channels[2].id) }
                if let first = player.channels.first {
                    player.beginScratch(first.id)
                    for i in 1...10 {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4 + Double(i) * 0.05) {
                            player.scratch(first.id, by: i <= 5 ? 0.12 : -0.08)
                            if i == 10 { player.endScratch(first.id); print("MIXER: scratch fertig, pos=\(player.channels.first?.position ?? -1)"); fflush(stdout) }
                        }
                    }
                }
                print("MIXER: kanäle=\(player.channels.map { "\($0.title) \(Int($0.level * 100))%" })"); fflush(stdout)
            }
        }
        if LaunchArgs.args.contains("--selftest") {
            // warten, bis das Fenster steht und der Delegate das Board kennt
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.runSelftest() }
        }
    }

    /// Fenster zu: je nach Einstellung beenden oder in der Menüleiste weiterlaufen (Kürzel gehen weiter)
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { !AppSettings.keepInMenuBar }

    private var windowObservers: [Any] = []

    /// Hauptfenster zu → aus dem Dock verschwinden, nur noch das Symbol in der Menüleiste; wieder auf → zurück ins Dock
    private func watchMainWindow() {
        let center = NotificationCenter.default
        windowObservers.append(center.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { note in
            guard (note.object as? NSWindow)?.identifier?.rawValue == "main" else { return }
            if AppSettings.keepInMenuBar { NSApp.setActivationPolicy(.accessory) } else { NSApp.terminate(nil) }
        })
        windowObservers.append(center.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { note in
            guard (note.object as? NSWindow)?.identifier?.rawValue == "main", NSApp.activationPolicy() != .regular else { return }
            NSApp.setActivationPolicy(.regular)
        })
    }

    func applicationWillTerminate(_ notification: Notification) {
        board?.lobby.leave() // Lobby sauber verlassen, fremde Sounds aus dem Cache räumen
    }

    /// Ganzes Fenster inkl. Titelleiste und Toolbar als PNG
    private static func snapshot(to path: String) {
        print("SNAPSHOT: fenster \(NSApp.windows.map { "\($0.identifier?.rawValue ?? "-") \(type(of: $0)) '\($0.title)' \(Int($0.frame.width))x\(Int($0.frame.height)) visible=\($0.isVisible)" }) policy=\(NSApp.activationPolicy().rawValue)")
        // mit --settings das Einstellungsfenster, sonst das Hauptfenster
        let wantSettings = LaunchArgs.args.contains("--open-settings")
        guard let window = NSApp.windows.first(where: { $0.isVisible && !($0 is NSPanel) && $0.frame.width > 200 && ($0.identifier?.rawValue == "main") != wantSettings }) else {
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
        // ⌥2: keyCode 19 – muss auch im Hintergrund gehen
        board.keys.set(KeyBind(keyCode: 19, flags: [.option]), for: sound.id)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            print("KEYTEST nachher: \(status()) blockiert=\(board.keys.binds[sound.id]?.blockedCharacter ?? "-")")
            fflush(stdout)
        }
        // Zeit, um von außen ⌥2 zu drücken (siehe Test); gespielt wird über den globalen Kürzel
        var fired = false
        for i in 1...40 {
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(i) * 0.1) {
                if !fired, !board.player.progress.isEmpty { fired = true; print("KEYTEST: gespielt \(board.player.progress.keys.sorted())"); fflush(stdout) }
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4.2) {
            if !fired { print("KEYTEST: nichts gespielt") }
            board.player.stopAll()
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
    @AppStorage(AppSettings.keepInMenuBarKey) private var keepInMenuBar = true

    init() {
        let board = Board(library: SoundLibrary(folder: LaunchArgs.library))
        AppDelegate.sharedBoard = board
        _board = StateObject(wrappedValue: board)
    }

    var body: some Scene {
        Window("ClipSound", id: "main") {
            ContentView(board: board, updater: updater)
                .frame(minWidth: 720, minHeight: 560)
                .onAppear { delegate.board = board }
                .background(SettingsOnLaunch())
                .task {
                    // Beim Start nach Updates schauen (nicht bei Testläufen)
                    let testRun = ["--snapshot", "--selftest", "--keytest", "--import"].contains(where: LaunchArgs.args.contains)
                    guard !testRun || LaunchArgs.args.contains("--pretend-version") else { return }
                    await updater.check()
                    if LaunchArgs.args.contains("--update-now"), updater.available != nil { await updater.install() }
                }
        }
        .defaultSize(width: 980, height: 640)
        .restorationBehavior(.disabled) // immer mit Fenster starten, auch wenn es beim Beenden zu war
        .commands {
            CommandGroup(after: .windowList) {
                OpenMainWindowButton(title: "ClipSound-Fenster")
                    .keyboardShortcut("1")
            }
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

        Settings {
            SettingsView()
        }

        MenuBarExtra("ClipSound", systemImage: "speaker.wave.2.fill", isInserted: $keepInMenuBar) {
            OpenMainWindowButton(title: "ClipSound öffnen")
            Button("Alles stoppen") { board.stopAll() }
            Divider()
            SettingsLink { Text("Einstellungen …") }
            Divider()
            Button("ClipSound beenden") { NSApp.terminate(nil) }
                .keyboardShortcut("q")
        }
    }
}

/// --open-settings: Einstellungen gleich nach dem Start öffnen (zum Testen)
private struct SettingsOnLaunch: View {
    @Environment(\.openSettings) private var openSettings
    var body: some View {
        Color.clear.task {
            guard LaunchArgs.args.contains("--open-settings") else { return }
            try? await Task.sleep(for: .seconds(1))
            openSettings()
        }
    }
}

struct OpenMainWindowButton: View {
    let title: String
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button(title) {
            NSApp.setActivationPolicy(.regular)
            openWindow(id: "main")
            NSApp.activate()
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
