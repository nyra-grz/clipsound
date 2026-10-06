import SwiftUI
import Combine
import UniformTypeIdentifiers

/// Verbindet Bibliothek, Player und Tastenkürzel
final class Board: ObservableObject {
    let library: SoundLibrary
    let player = SoundPlayer()
    let lobby = Lobby()
    let keys: KeyBindStore
    private let hotKeys = GlobalHotKeys.shared

    @Published var query = ""
    @Published var searchFocused = false
    @Published var problem: String?
    /// Sound, für den gerade eine Taste aufgenommen wird
    @Published var recording: Sound?
    @Published var recorderNote: String?
    /// Erhöht sich, wenn sich die globalen Kürzel ändern (für die Anzeige)
    @Published private(set) var hotKeyRevision = 0

    private var subscriptions: Set<AnyCancellable> = []

    init(library: SoundLibrary) {
        self.library = library
        keys = KeyBindStore(file: library.settingsFolder.appendingPathComponent("keybinds.json"))

        hotKeys.onPress = { [weak self] id in
            guard let self, self.recording == nil, let sound = self.library.sounds.first(where: { $0.id == id }) else { return }
            self.play(sound)
        }
        library.$sounds
            .sink { [weak self] sounds in
                self?.keys.sync(with: sounds)
                self?.lobby.hostLibraryChanged(sounds)
            }
            .store(in: &subscriptions)

        // Lobby: Sounds starten erst, wenn der Server sie an alle verteilt hat
        lobby.onPlay = { [weak self] sound, delay in
            guard let self else { return }
            if !self.player.play(sound, delay: delay) { self.problem = "„\(sound.title)“ konnte nicht abgespielt werden." }
        }
        lobby.onStop = { [weak self] in self?.player.stopAll() }
        lobby.onReceive = { [weak self] url in self?.importItems([url]) }
        // Änderungen der Lobby an die Oberfläche weiterreichen
        lobby.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &subscriptions)
        keys.$binds
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.registerHotKeys() }
            .store(in: &subscriptions)
    }

    var visible: [Sound] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? library.sounds : library.sounds.filter { $0.id.lowercased().contains(q) || $0.title.lowercased().contains(q) }
    }

    /// Gast in einer fremden Lobby: dann zeigt das Fenster die Sounds des Hosts
    var inGuestLobby: Bool { lobby.active && !lobby.isHost }

    var visibleLobby: [LobbySound] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? lobby.sounds : lobby.sounds.filter { $0.title.lowercased().contains(q) }
    }

    func play(_ sound: Sound) {
        // Als Host läuft alles über die Lobby, damit es bei allen gleichzeitig startet
        if lobby.active && lobby.isHost {
            if lobby.play(own: sound) { return }
            lobby.note = "„\(sound.title)“ ist noch nicht in der Lobby – nur bei dir abgespielt."
        }
        if !player.play(sound) { problem = "„\(sound.title)“ konnte nicht abgespielt werden." }
    }

    func play(_ sound: LobbySound) { lobby.play(sound) }

    func stopAll() {
        player.stopAll()
        if lobby.active { lobby.stopAll() }
    }

    func playFirstVisible() {
        if inGuestLobby { if let first = visibleLobby.first { play(first) } }
        else if let first = visible.first { play(first) }
    }

    func delete(_ sound: Sound) {
        keys.set(nil, for: sound.id)
        library.delete(sound)
    }

    func hotKeyState(for sound: Sound) -> HotKeyState {
        guard keys.binds[sound.id]?.isGlobal == true else { return .local }
        if hotKeys.active.contains(sound.id) { return .global }
        return hotKeys.failed.contains(sound.id) ? .failed : .local
    }

    enum HotKeyState { case local, global, failed }

    private func registerHotKeys() {
        guard recording == nil else { return }
        hotKeys.register(keys.binds)
        hotKeyRevision += 1
    }

    // MARK: Taste aufnehmen

    func startRecording(_ sound: Sound) {
        hotKeys.unregisterAll() // sonst würde eine schon vergebene Kombi abspielen statt aufnehmen
        recorderNote = nil
        recording = sound
    }

    func stopRecording() {
        recording = nil
        recorderNote = nil
        registerHotKeys()
    }

    func removeBind() {
        guard let sound = recording else { return }
        keys.set(nil, for: sound.id)
        stopRecording()
    }

    private func record(_ event: NSEvent) {
        guard let sound = recording else { return }
        let bind = KeyBind(event: event)
        if bind.flags.contains(.command) && bind.flags.isDisjoint(with: [.control, .option]) {
            recorderNote = "⌘ allein ist für Menübefehle reserviert – nimm ⌃ oder ⌥ dazu."
            return
        }
        if let previous = keys.set(bind, for: sound.id) {
            let name = library.sounds.first { $0.id == previous }?.title ?? previous
            recorderNote = "\(bind.display) war bei „\(name)“ – dort ist sie jetzt entfernt."
            return // offen lassen, damit man den Hinweis sieht
        }
        if let char = bind.blockedCharacter {
            recorderNote = "Gespeichert. Hinweis: \(bind.display) tippt sonst „\(char)“ – das geht in anderen Apps nicht mehr, solange ClipSound läuft."
            return
        }
        stopRecording()
    }

    // MARK: Import

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

    /// Tasten im ClipSound-Fenster – liefert true, wenn die Taste verbraucht wurde
    func handleKey(_ event: NSEvent, typingInSearch: Bool) -> Bool {
        if recording != nil {
            switch event.keyCode {
            case 53: stopRecording()                 // Esc: abbrechen
            case 51, 117: removeBind()               // ⌫ / ⌦: Taste entfernen
            default: record(event)
            }
            return true
        }
        if event.keyCode == 53 { // Esc stoppt alles, im Suchfeld darf Esc zusätzlich leeren
            stopAll()
            return !typingInSearch
        }
        guard !typingInSearch else { return false }
        if event.modifierFlags.contains(.command) { return false } // Menübefehle
        if let id = keys.soundID(for: event),
           !hotKeys.active.contains(id), // globale Kürzel feuern schon über Carbon
           let sound = library.sounds.first(where: { $0.id == id }) {
            play(sound)
            return true
        }
        if event.charactersIgnoringModifiers == "/" { searchFocused = true; return true }
        return false
    }
}

struct ContentView: View {
    @ObservedObject var board: Board
    @ObservedObject var library: SoundLibrary
    @ObservedObject var player: SoundPlayer
    @ObservedObject var keys: KeyBindStore
    @ObservedObject var updater: Updater
    @State private var dropTargeted = false
    @State private var updateDismissed = false
    @State private var pendingDelete: Sound?
    @State private var showLobby = false

    init(board: Board, updater: Updater) {
        self.board = board
        self.library = board.library
        self.player = board.player
        self.keys = board.keys
        self.updater = updater
    }

    var body: some View {
        Group {
            if board.inGuestLobby {
                lobbyGrid
            } else if library.sounds.isEmpty {
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
        .safeAreaInset(edge: .top, spacing: 0) {
            VStack(spacing: 0) {
                updateBar
                if board.lobby.active { LobbyBar(lobby: board.lobby, showLobby: $showLobby) }
            }
        }
        .overlay(alignment: .bottom) { LobbyNote(lobby: board.lobby) }
        .sheet(isPresented: $showLobby) { LobbySheet(lobby: board.lobby, library: library) }
        .alert(requestTitle, isPresented: requestShown, presenting: board.lobby.requests.first) { request in
            Button(request.kind == .asked ? "Erlauben" : "Annehmen") { board.lobby.answer(request, ok: true) }
            Button("Ablehnen", role: .cancel) { board.lobby.answer(request, ok: false) }
        } message: { request in
            Text(request.kind == .asked
                 ? "Der Sound wird in die Bibliothek von \(request.by) kopiert."
                 : "Der Sound wird in deine Bibliothek kopiert.")
        }
        .background(.background)
        .overlay { if dropTargeted { dropHighlight } }
        .overlay { if let sound = board.recording { recorder(for: sound) } }
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            loadDropped(providers)
            return true
        }
        .navigationTitle("ClipSound")
        .navigationSubtitle(subtitle)
        .searchable(text: $board.query, isPresented: $board.searchFocused, placement: .toolbar, prompt: "Suchen")
        .onSubmit(of: .search) { board.playFirstVisible() }
        .toolbar { toolbar }
        .confirmationDialog("„\(pendingDelete?.title ?? "")“ löschen?", isPresented: deleteDialogShown, presenting: pendingDelete) { sound in
            Button("In den Papierkorb legen", role: .destructive) { board.delete(sound) }
        } message: { _ in
            Text("Die Datei wird in den Papierkorb verschoben.")
        }
        .alert("Updates", isPresented: updateMessageShown) {
            Button("OK") { updater.state = .idle }
            if case .failed = updater.state { Button("Download-Seite öffnen") { updater.openReleasePage(); updater.state = .idle } }
        } message: {
            switch updater.state {
            case .upToDate: Text("Du hast die neueste Version (\(updater.currentVersion)).")
            case .failed(let text): Text(text)
            default: Text("")
            }
        }
        .alert("Lobby", isPresented: lobbyProblemShown, presenting: board.lobby.problem) { _ in
            Button("OK") {}
        } message: { text in
            Text(text)
        }
        .alert("ClipSound", isPresented: problemShown, presenting: board.problem) { _ in
            Button("OK") {}
        } message: { text in
            Text(text)
        }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 180), spacing: 10)], spacing: 10) {
                ForEach(board.visible) { sound in
                    PadView(sound: sound,
                            bind: keys.binds[sound.id],
                            hotKey: board.hotKeyState(for: sound),
                            progress: player.progress[sound.id],
                            onPlay: { board.play(sound) },
                            onEditKey: { board.startRecording(sound) })
                    .contextMenu {
                        Button("Abspielen", systemImage: "play") { board.play(sound) }
                        Button("Taste festlegen …", systemImage: "keyboard") { board.startRecording(sound) }
                        Button("Im Finder zeigen", systemImage: "folder") { library.revealInFinder(sound) }
                        if board.lobby.isHost && !board.lobby.guests.isEmpty {
                            Menu("Schenken an", systemImage: "gift") {
                                ForEach(board.lobby.guests) { member in
                                    Button(member.name) { board.lobby.offer(sound, to: member) }
                                }
                            }
                        }
                        Divider()
                        Button("Löschen …", systemImage: "trash", role: .destructive) { pendingDelete = sound }
                    }
                }
            }
            .padding(16)
            .id(board.hotKeyRevision)
        }
    }

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Button("Importieren", systemImage: "plus", action: board.openImportPanel)
                .help("Sound-Dateien oder einen Ordner importieren (⌘O)")

            Button("Alles stoppen", systemImage: "stop.fill", action: board.stopAll)
                .help(board.lobby.active ? "Bei allen in der Lobby stoppen (Esc)" : "Alles stoppen (Esc)")
                .disabled(player.progress.isEmpty && !board.lobby.active)

            Toggle(isOn: $player.overlap) {
                Label("Überlappen", systemImage: "square.stack.3d.up")
            }
            .help("Mehrere Sounds gleichzeitig abspielen")
        }
        ToolbarItem(placement: .primaryAction) {
            VolumeControl(volume: $player.volume)
        }
        ToolbarItem(placement: .navigation) {
            Button { showLobby = true } label: {
                Label("Lobby", systemImage: board.lobby.active ? "person.2.fill" : "person.2")
            }
            .help("Zusammen abspielen: Lobby öffnen oder mit Code beitreten")
        }
    }

    // MARK: Taste aufnehmen

    private func recorder(for sound: Sound) -> some View {
        let bind = keys.binds[sound.id]
        return ZStack {
            Color.black.opacity(0.25).ignoresSafeArea()
                .onTapGesture { board.stopRecording() }
            VStack(spacing: 14) {
                Text("Taste für „\(sound.title)“")
                    .font(.headline)
                    .multilineTextAlignment(.center)
                Text(bind?.display ?? "–")
                    .font(.system(size: 28, weight: .medium).monospaced())
                    .frame(minWidth: 90, minHeight: 54)
                    .padding(.horizontal, 14)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                Text("Drück eine Taste oder Kombination.")
                    .foregroundStyle(.secondary)
                Label("Mit ⌃ (ctrl) oder ⌥ (alt) geht sie überall, auch wenn ClipSound im Hintergrund ist – z. B. in Spielen oder Discord.",
                      systemImage: "globe")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: 300)
                if let note = board.recorderNote {
                    Text(note)
                        .font(.callout)
                        .foregroundStyle(.orange)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 300)
                }
                HStack {
                    Button("Taste entfernen", action: board.removeBind)
                        .disabled(bind == nil)
                        .help("⌫")
                    Spacer()
                    Button("Fertig", action: board.stopRecording)
                        .keyboardShortcut(.defaultAction)
                        .help("Esc")
                }
                .frame(width: 300)
            }
            .padding(24)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .shadow(color: .black.opacity(0.2), radius: 20, y: 8)
        }
    }

    private var dropHighlight: some View {
        RoundedRectangle(cornerRadius: 12)
            .strokeBorder(Color.accentColor, lineWidth: 3)
            .background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
            .padding(8)
            .allowsHitTesting(false)
    }

    // MARK: Update

    @ViewBuilder private var updateBar: some View {
        if let release = updater.available, !updateDismissed || updater.state == .downloading {
            VStack(spacing: 0) {
                HStack(spacing: 10) {
                    Image(systemName: "arrow.down.circle.fill")
                        .foregroundStyle(Color.accentColor)
                        .font(.title3)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("ClipSound \(release.version) ist verfügbar").font(.headline)
                        Text("Du hast \(updater.currentVersion).").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if updater.state == .downloading {
                        ProgressView().controlSize(.small)
                        Text("Wird installiert …").foregroundStyle(.secondary)
                    } else {
                        Button("Was ist neu?") { updater.openReleasePage() }
                            .buttonStyle(.link)
                        Button("Später") { updateDismissed = true }
                        Button("Jetzt aktualisieren") { Task { await updater.install() } }
                            .buttonStyle(.borderedProminent)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                Divider()
            }
            .background(.bar)
        }
    }

    private var updateMessageShown: Binding<Bool> {
        Binding(get: {
            switch updater.state { case .upToDate, .failed: true; default: false }
        }, set: { if !$0 { updater.state = .idle } })
    }

    private var deleteDialogShown: Binding<Bool> {
        Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } })
    }

    private var subtitle: String {
        let n = board.inGuestLobby ? board.lobby.sounds.count : library.sounds.count
        let count = n == 1 ? "1 Sound" : "\(n) Sounds"
        return board.inGuestLobby ? "Lobby von \(board.lobby.hostName) · \(count)" : count
    }

    private var requestTitle: String {
        guard let r = board.lobby.requests.first else { return "" }
        return r.kind == .asked ? "\(r.by) möchte „\(r.soundTitle)“ behalten" : "\(r.by) schenkt dir „\(r.soundTitle)“"
    }

    private var requestShown: Binding<Bool> {
        Binding(get: { !board.lobby.requests.isEmpty }, set: { _ in })
    }

    private var lobbyProblemShown: Binding<Bool> {
        Binding(get: { board.lobby.problem != nil && !showLobby }, set: { if !$0 { board.lobby.problem = nil } })
    }

    // MARK: Lobby als Gast

    @ViewBuilder private var lobbyGrid: some View {
        if board.lobby.sounds.isEmpty {
            ContentUnavailableView {
                Label(board.lobby.phase == .open ? "Noch keine Sounds" : "Verbinde …", systemImage: "person.2")
            } description: {
                Text(board.lobby.phase == .open ? "\(board.lobby.hostName) hat noch keine Sounds freigegeben." : "Einen Moment.")
            }
        } else if board.visibleLobby.isEmpty {
            ContentUnavailableView.search(text: board.query)
        } else {
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 180), spacing: 10)], spacing: 10) {
                    ForEach(board.visibleLobby) { sound in
                        PadView(sound: Sound(url: URL(fileURLWithPath: sound.name)),
                                bind: nil,
                                hotKey: .local,
                                progress: sound.local.flatMap { player.progress[$0.id] },
                                loading: sound.local == nil,
                                onPlay: { board.play(sound) },
                                onEditKey: {})
                        .contextMenu {
                            Button("Abspielen", systemImage: "play") { board.play(sound) }
                            Button("Behalten …", systemImage: "square.and.arrow.down") { board.lobby.ask(sound) }
                        }
                    }
                }
                .padding(16)
            }
        }
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
    let bind: KeyBind?
    let hotKey: Board.HotKeyState
    let progress: Double?
    var loading = false
    let onPlay: () -> Void
    let onEditKey: () -> Void

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

        Button(action: onPlay) {
            VStack(alignment: .leading, spacing: 0) {
                Image(systemName: playing ? "speaker.wave.2.fill" : "waveform")
                    .symbolEffect(.variableColor.iterative, isActive: playing)
                    .font(.system(size: 15, weight: .medium))
                    .foregroundStyle(tint)
                    .frame(width: 22, height: 20, alignment: .leading)
                Spacer(minLength: 6)
                Text(sound.title)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.primary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(10)
            .frame(height: 86)
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
        .opacity(loading ? 0.5 : 1)
        .overlay(alignment: .topTrailing) { if loading { ProgressView().controlSize(.mini).padding(10) } }
        // Tasten-Badge liegt außerhalb des Kachel-Buttons, damit er eigene Klicks bekommt
        .overlay(alignment: .topTrailing) { keyBadge.padding(8) }
        .onHover { hovering = $0 }
        .help(sound.id)
    }

    @ViewBuilder private var keyBadge: some View {
        if let bind {
            Button(action: onEditKey) {
                HStack(spacing: 3) {
                    if hotKey == .global {
                        Image(systemName: "globe").font(.system(size: 9, weight: .semibold))
                    } else if hotKey == .failed {
                        Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 9)).foregroundStyle(.orange)
                    }
                    Text(bind.display).font(.system(size: 11, weight: .medium).monospaced())
                }
                .foregroundStyle(.secondary)
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 4, style: .continuous))
            }
            .buttonStyle(.plain)
            .help(badgeHelp)
        } else if hovering {
            Button(action: onEditKey) {
                Image(systemName: "keyboard")
                    .font(.system(size: 11))
                    .foregroundStyle(.tertiary)
                    .padding(.horizontal, 5)
                    .padding(.vertical, 2)
            }
            .buttonStyle(.plain)
            .help("Taste festlegen")
        }
    }

    private var badgeHelp: String {
        switch hotKey {
        case .global: "Funktioniert auch im Hintergrund · Klicken zum Ändern"
        case .failed: "Diese Kombination nutzt schon eine andere App – nur im ClipSound-Fenster aktiv · Klicken zum Ändern"
        case .local: "Nur im ClipSound-Fenster – mit ⌃ oder ⌥ geht sie überall · Klicken zum Ändern"
        }
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
