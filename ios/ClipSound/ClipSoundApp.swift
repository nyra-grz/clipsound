import AVFoundation
import SwiftUI

@main
struct ClipSoundApp: App {
    @StateObject private var library = SoundLibrary()
    @StateObject private var player = SoundPlayer()
    @StateObject private var lobby = Lobby()
    @Environment(\.scenePhase) private var scenePhase

    init() {
        // Auch bei Stummschalter hörbar; Musik aus anderen Apps läuft weiter
        try? AVAudioSession.sharedInstance().setCategory(.playback, options: [.mixWithOthers])
        try? AVAudioSession.sharedInstance().setActive(true)
    }

    var body: some Scene {
        WindowGroup {
            ContentView(library: library, player: player, lobby: lobby)
                .onAppear(perform: connectLobby)
                .onReceive(library.$sounds) { lobby.hostLibraryChanged($0) }
        }
        .onChange(of: scenePhase) { _, phase in
            // Dateien könnten über die Dateien-App dazugekommen sein
            if phase == .active { library.reload() }
        }
    }

    /// Lobby-Ereignisse an Player und Bibliothek hängen
    private func connectLobby() {
        lobby.onPlay = { [player] sound, delay in _ = player.play(sound, delay: delay) }
        lobby.onStop = { [player] in player.stopAll() }
        lobby.onReceive = { [library, lobby] url in
            let r = library.importItems([url])
            if r.added == 0 { lobby.note = "Konnte nicht gespeichert werden." }
        }
        // Testen im Simulator: --lobby-join CODE bzw. --lobby-host
        let args = ProcessInfo.processInfo.arguments
        if let i = args.firstIndex(of: "--lobby-join"), i + 1 < args.count {
            lobby.join(args[i + 1])
        } else if args.contains("--lobby-host") {
            lobby.open(with: library.sounds)
        }
    }
}
