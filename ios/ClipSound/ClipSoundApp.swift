import AVFoundation
import SwiftUI

@main
struct ClipSoundApp: App {
    @StateObject private var library = SoundLibrary()
    @StateObject private var player = SoundPlayer()
    @Environment(\.scenePhase) private var scenePhase

    init() {
        // Auch bei Stummschalter hörbar; Musik aus anderen Apps läuft weiter
        try? AVAudioSession.sharedInstance().setCategory(.playback, options: [.mixWithOthers])
        try? AVAudioSession.sharedInstance().setActive(true)
    }

    var body: some Scene {
        WindowGroup {
            ContentView(library: library, player: player)
        }
        .onChange(of: scenePhase) { _, phase in
            // Dateien könnten über die Dateien-App dazugekommen sein
            if phase == .active { library.reload() }
        }
    }
}
