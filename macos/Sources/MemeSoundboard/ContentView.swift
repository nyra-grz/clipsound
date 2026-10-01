import SwiftUI
import UniformTypeIdentifiers

let shortcutKeys: [Character] = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "q", "w", "e", "r", "t", "z", "u", "i", "o", "p"]

/// Hält den Suchtext und verbindet Bibliothek + Player
final class Board: ObservableObject {
    let library: SoundLibrary
    let player = SoundPlayer()
    @Published var query = ""
    @Published var searchFocused = false
    @Published var problem: String?

    init(library: SoundLibrary) { self.library = library }

    var visible: [Sound] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? library.sounds : library.sounds.filter { $0.id.lowercased().contains(q) || $0.title.lowercased().contains(q) }
    }

    func play(_ sound: Sound) {
        if !player.play(sound) { problem = "„\(sound.title)“ konnte nicht abgespielt werden." }
    }

    func importItems(_ urls: [URL]) {
        let result = library.importItems(urls)
        if !result.rejected.isEmpty {
            let list = result.rejected.prefix(5).joined(separator: "\n")
            let more = result.rejected.count > 5 ? "\n… und \(result.rejected.count - 5) weitere" : ""
            problem = "Diese Dateien sind keine Sounds und wurden übersprungen:\n\n\(list)\(more)"
        }
    }

    func openImportPanel() {
        let panel = NSOpenPanel()
        panel.message = "Wähle Sound-Dateien oder einen ganzen Ordner."
        panel.prompt = "Importieren"
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = [.audio, .folder]
        if panel.runModal() == .OK { importItems(panel.urls) }
    }

    /// Tastenkürzel 1–0, Q–P, Esc und / – liefert true, wenn die Taste verbraucht wurde
    func handleKey(_ event: NSEvent, typingInSearch: Bool) -> Bool {
        if event.keyCode == 53 { // Esc: stoppt alles, im Suchfeld darf Esc zusätzlich leeren
            player.stopAll()
            return !typingInSearch
        }
        guard !typingInSearch,
              event.modifierFlags.intersection([.command, .control, .option]).isEmpty,
              let ch = event.charactersIgnoringModifiers?.lowercased().first else { return false }
        if ch == "/" { searchFocused = true; return true }
        if let i = shortcutKeys.firstIndex(of: ch), i < visible.count { play(visible[i]); return true }
        return false
    }
}

struct ContentView: View {
    @ObservedObject var board: Board
    @ObservedObject var library: SoundLibrary
    @ObservedObject var player: SoundPlayer
    @State private var dropTargeted = false
    @State private var pendingDelete: Sound?

    init(board: Board) {
        self.board = board
        self.library = board.library
        self.player = board.player
    }

    var body: some View {
        Group {
            if library.sounds.isEmpty {
                ContentUnavailableView {
                    Label("Keine Sounds", systemImage: "waveform")
                } description: {
                    Text("Zieh Sound-Dateien oder einen ganzen Ordner in dieses Fenster.")
                } actions: {
                    Button("Sounds importieren …", action: board.openImportPanel)
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                }
            } else if board.visible.isEmpty {
                ContentUnavailableView.search(text: board.query)
            } else {
                grid
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.background)
        .overlay { if dropTargeted { dropHighlight } }
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            loadDropped(providers)
            return true
        }
        .navigationTitle("Meme Soundboard")
        .navigationSubtitle(library.sounds.count == 1 ? "1 Sound" : "\(library.sounds.count) Sounds")
        .searchable(text: $board.query, isPresented: $board.searchFocused, placement: .toolbar, prompt: "Suchen")
        .onSubmit(of: .search) { if let first = board.visible.first { board.play(first) } }
        .toolbar { toolbar }
        .confirmationDialog("„\(pendingDelete?.title ?? "")“ löschen?", isPresented: deleteDialogShown, presenting: pendingDelete) { sound in
            Button("In den Papierkorb legen", role: .destructive) { library.delete(sound) }
        } message: { _ in
            Text("Die Datei wird in den Papierkorb verschoben.")
        }
        .alert("Import", isPresented: problemShown, presenting: board.problem) { _ in
            Button("OK") {}
        } message: { text in
            Text(text)
        }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 180), spacing: 10)], spacing: 10) {
                ForEach(Array(board.visible.enumerated()), id: \.element.id) { i, sound in
                    PadView(sound: sound,
                            key: i < shortcutKeys.count ? String(shortcutKeys[i]).uppercased() : nil,
                            progress: player.progress[sound.id]) {
                        board.play(sound)
                    }
                    .contextMenu {
                        Button("Abspielen", systemImage: "play") { board.play(sound) }
                        Button("Im Finder zeigen", systemImage: "folder") { library.revealInFinder(sound) }
                        Divider()
                        Button("Löschen …", systemImage: "trash", role: .destructive) { pendingDelete = sound }
                    }
                }
            }
            .padding(16)
        }
    }

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Button("Importieren", systemImage: "plus", action: board.openImportPanel)
                .help("Sound-Dateien oder einen Ordner importieren (⌘O)")

            Button("Alles stoppen", systemImage: "stop.fill", action: player.stopAll)
                .help("Alles stoppen (Esc)")
                .disabled(player.progress.isEmpty)

            Toggle(isOn: $player.overlap) {
                Label("Überlappen", systemImage: "square.stack.3d.up")
            }
            .help("Mehrere Sounds gleichzeitig abspielen")
        }
        ToolbarItem(placement: .primaryAction) {
            VolumeControl(volume: $player.volume)
        }
    }

    private var dropHighlight: some View {
        RoundedRectangle(cornerRadius: 12)
            .strokeBorder(Color.accentColor, lineWidth: 3)
            .background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
            .padding(8)
            .allowsHitTesting(false)
    }

    private var deleteDialogShown: Binding<Bool> {
        Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } })
    }

    private var problemShown: Binding<Bool> {
        Binding(get: { board.problem != nil }, set: { if !$0 { board.problem = nil } })
    }

    private func loadDropped(_ providers: [NSItemProvider]) {
        let group = DispatchGroup()
        var urls: [URL] = []
        let lock = NSLock()
        for provider in providers {
            group.enter()
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                if let url { lock.lock(); urls.append(url); lock.unlock() }
                group.leave()
            }
        }
        group.notify(queue: .main) { board.importItems(urls) }
    }
}

// MARK: - Lautstärke

struct VolumeControl: View {
    @Binding var volume: Double

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "speaker.wave.2.fill", variableValue: min(volume, 1))
                .foregroundStyle(.secondary)
                .frame(width: 18)
            Slider(value: $volume, in: 0...SoundPlayer.maxVolume)
                .controlSize(.small)
                .frame(width: 110)
            Text(volume, format: .percent.precision(.fractionLength(0)))
                .monospacedDigit()
                .foregroundStyle(volume > 1 ? Color.orange : Color.secondary)
                .frame(width: 42, alignment: .trailing)
        }
        .padding(.horizontal, 4)
        .help(volume > 1 ? "Verstärkt – über 100 % kann es übersteuern" : "Lautstärke")
    }
}

// MARK: - Kachel

struct PadView: View {
    let sound: Sound
    let key: String?
    let progress: Double?
    let action: () -> Void

    @State private var hovering = false

    /// Feste Systemfarbe pro Sound, damit man Kacheln wiedererkennt
    private var tint: Color {
        let colors: [Color] = [.blue, .purple, .pink, .orange, .green, .teal, .indigo, .red]
        var h: UInt32 = 0
        for unit in sound.id.utf16 { h = h &* 31 &+ UInt32(unit) }
        return colors[Int(h % UInt32(colors.count))]
    }

    var body: some View {
        let playing = progress != nil
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)

        Button(action: action) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top) {
                    Image(systemName: playing ? "speaker.wave.2.fill" : "waveform")
                        .symbolEffect(.variableColor.iterative, isActive: playing)
                        .font(.system(size: 15, weight: .medium))
                        .foregroundStyle(tint)
                        .frame(width: 22, height: 18, alignment: .leading)
                    Spacer(minLength: 4)
                    if let key {
                        Text(key)
                            .font(.system(size: 11, weight: .medium).monospaced())
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 5)
                            .padding(.vertical, 1)
                            .background(.quaternary, in: RoundedRectangle(cornerRadius: 4, style: .continuous))
                    }
                }
                Spacer(minLength: 6)
                Text(sound.title)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.primary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(10)
            .frame(height: 76)
            .background {
                shape.fill(playing ? AnyShapeStyle(tint.opacity(0.14)) : AnyShapeStyle(hovering ? .quaternary : .quinary))
            }
            .overlay(alignment: .bottom) {
                if let progress {
                    GeometryReader { geo in
                        Capsule().fill(tint)
                            .frame(width: max(4, geo.size.width * progress), height: 3)
                    }
                    .frame(height: 3)
                    .padding(.horizontal, 10)
                    .padding(.bottom, 4)
                }
            }
            .overlay { shape.strokeBorder(playing ? tint.opacity(0.7) : Color.primary.opacity(0.08), lineWidth: 1) }
            .contentShape(shape)
        }
        .buttonStyle(PadButtonStyle())
        .onHover { hovering = $0 }
        .help(sound.id)
    }
}

struct PadButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.75 : 1)
            .scaleEffect(configuration.isPressed ? 0.98 : 1)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
    }
}
