import SwiftUI
import Combine
import UniformTypeIdentifiers

/// Verbindet Bibliothek, Player, Tastenkürzel und Bilder
final class Board: ObservableObject {
    let library: SoundLibrary
    let player = SoundPlayer()
    let lobby = Lobby()
    let keys: KeyBindStore
    let images: SoundImages
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
        images = SoundImages(folder: library.settingsFolder.appendingPathComponent("Bilder", isDirectory: true))

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
        lobby.systemVolume = (get: SystemVolume.get, set: SystemVolume.set)
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

    /// Enter im Suchfeld: ersten Treffer abspielen (auf der Lobby-Seite die Sounds des Hosts)
    func playFirstVisible(inLobby: Bool) {
        if inLobby { if let first = visibleLobby.first { play(first) } }
        else if let first = visible.first { play(first) }
    }

    func delete(_ sound: Sound) {
        keys.set(nil, for: sound.id)
        images.remove(sound.id)
        library.delete(sound)
    }

    // MARK: Bilder

    func chooseImage(for sound: Sound) {
        let panel = NSOpenPanel()
        panel.message = "Bild für „\(sound.title)“ auswählen"
        panel.prompt = "Auswählen"
        panel.allowedContentTypes = [.image]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if !images.set(contentsOf: url, for: sound.id) { problem = "„\(url.lastPathComponent)“ ist kein Bild, das ClipSound lesen kann." }
    }

    func pasteImage(for sound: Sound) {
        guard let image = NSImage(pasteboard: .general) else { problem = "In der Zwischenablage ist kein Bild."; return }
        if !images.set(image, for: sound.id) { problem = "Das Bild aus der Zwischenablage konnte nicht übernommen werden." }
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

/// Seiten in der Seitenleiste
enum Page: Hashable {
    case sounds, lobby, account
    case clan(String)

    /// Startargument --page sounds|lobby|account|clan (clan = erster Clan)
    init?(launchArg: String?) {
        switch launchArg {
        case "sounds": self = .sounds
        case "lobby": self = .lobby
        case "account": self = .account
        case "clan": self = .clan("")
        default: return nil
        }
    }
}

/// Größe der Sound-Kacheln (Darstellung → Kachelgröße, ⌘+ / ⌘-)
enum TileSize: String, CaseIterable, Identifiable {
    case small, medium, large
    static let key = "tileSize"
    var id: Self { self }

    var title: String {
        switch self { case .small: "Klein"; case .medium: "Mittel"; case .large: "Groß" }
    }
    var minWidth: CGFloat { switch self { case .small: 150; case .medium: 188; case .large: 236 } }
    var height: CGFloat { switch self { case .small: 92; case .medium: 112; case .large: 140 } }
    var radius: CGFloat { switch self { case .small: 16; case .medium: 20; case .large: 24 } }
    var font: CGFloat { switch self { case .small: 13; case .medium: 14; case .large: 16 } }
    var symbol: CGFloat { switch self { case .small: 16; case .medium: 19; case .large: 23 } }
    var padding: CGFloat { switch self { case .small: 11; case .medium: 13; case .large: 15 } }

    var bigger: TileSize? { switch self { case .small: .medium; case .medium: .large; case .large: nil } }
    var smaller: TileSize? { switch self { case .small: nil; case .medium: .small; case .large: .medium } }
}

struct ContentView: View {
    @ObservedObject var board: Board
    @ObservedObject var library: SoundLibrary
    @ObservedObject var player: SoundPlayer
    @ObservedObject var keys: KeyBindStore
    @ObservedObject var updater: Updater
    @ObservedObject var images: SoundImages
    @ObservedObject private var account = Account.shared
    @State private var page: Page? = Page(launchArg: LaunchArgs.value("--page")) ?? .sounds
    @State private var dropTargeted = false
    /// Beim Ziehen: Kachel unter dem Mauszeiger (ein Bild landet dort)
    @State private var dropTile: String?
    /// Wo die Kacheln gerade im Fenster liegen (Koordinatenraum „drop“)
    @State private var tileFrames: [String: CGRect] = [:]
    @State private var updateDismissed = false
    @State private var pendingDelete: Sound?
    @AppStorage(TileSize.key) private var tileSize: TileSize = .medium

    init(board: Board, updater: Updater) {
        self.board = board
        self.library = board.library
        self.player = board.player
        self.keys = board.keys
        self.images = board.images
        self.updater = updater
    }

    var body: some View {
        NavigationSplitView {
            Sidebar(page: $page, library: library, lobby: board.lobby, account: account)
        } detail: {
            detail
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .edgeBar(.top) { updateBanner }
                .edgeBar(.bottom) {
                    VStack(spacing: 10) {
                        LobbyNote(lobby: board.lobby)
                        PlayerBar(board: board, player: player, lobby: board.lobby) { page = .lobby }
                    }
                    .padding(.horizontal, 16)
                    .padding(.bottom, 14)
                }
        }
        .coordinateSpace(.named("drop"))
        .onPreferenceChange(TileFrameKey.self) { tileFrames = $0 }
        .onAppear { account.attach(library) } // Clan-Sounds von diesem Mac aus weitergeben
        // --page clan: ersten Clan zeigen, sobald die Clans geladen sind
        .onChange(of: account.clans) { _, clans in
            if case .clan("") = page, let first = clans.first { page = .clan(first.id) }
        }
        // Lobby geöffnet, beigetreten oder Clan-Session gestartet: dorthin wechseln
        .onChange(of: board.lobby.phase) { old, new in if old == .off && new != .off { page = .lobby } }
        .onChange(of: account.loggedIn) { _, loggedIn in
            if !loggedIn, case .clan = page { page = .account }
        }
        // ⌘F oder „/“ auf einer Seite ohne Suche: zu den Sounds
        .onChange(of: board.searchFocused) { _, focused in
            if focused && page != .sounds && !(page == .lobby && board.inGuestLobby) { page = .sounds }
        }
        .alert(requestTitle, isPresented: requestShown, presenting: board.lobby.requests.first) { request in
            Button(request.kind == .asked ? "Erlauben" : "Annehmen") { board.lobby.answer(request, ok: true) }
            Button("Ablehnen", role: .cancel) { board.lobby.answer(request, ok: false) }
        } message: { request in
            Text(request.kind == .asked
                 ? "Der Sound wird in die Bibliothek von \(request.by) kopiert."
                 : "Der Sound wird in deine Bibliothek kopiert.")
        }
        .overlay { if dropTargeted { dropHighlight } }
        .overlay { if let sound = board.recording { recorder(for: sound) } }
        .animation(.spring(response: 0.35, dampingFraction: 0.85), value: board.recording)
        .onDrop(of: [.fileURL, .image], delegate: SoundDrop(targeted: $dropTargeted, tile: $dropTile,
                                                            tileAt: tile(at:), perform: handleDrop))
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

    @ViewBuilder private var detail: some View {
        switch page ?? .sounds {
        case .sounds:
            soundsPage
        case .lobby:
            LobbyPage(board: board, lobby: board.lobby, library: library, player: player, tileSize: tileSize)
        case .account:
            AccountPage(account: account) { page = .clan($0) }
        case .clan(let id):
            if let clan = account.clans.first(where: { $0.id == id }) {
                ClanPage(account: account, lobby: board.lobby, library: library, clan: clan) { page = .account }
                    .id(id)
            } else {
                ContentUnavailableView("Clan nicht gefunden", systemImage: "person.3",
                                       description: Text("Vielleicht bist du nicht mehr Mitglied."))
            }
        }
    }

    // MARK: Sounds

    private var soundsPage: some View {
        Group {
            if library.sounds.isEmpty {
                ContentUnavailableView {
                    Label("Keine Sounds", systemImage: "waveform")
                } description: {
                    Text("Zieh Sound-Dateien oder einen ganzen Ordner in dieses Fenster.")
                } actions: {
                    Button("Sounds importieren …", action: board.openImportPanel)
                        .glassButton(prominent: true)
                        .controlSize(.large)
                }
            } else if board.visible.isEmpty {
                ContentUnavailableView.search(text: board.query)
            } else {
                grid
            }
        }
        .navigationTitle("Sounds")
        .navigationSubtitle(library.sounds.count == 1 ? "1 Sound" : "\(library.sounds.count) Sounds")
        .searchable(text: $board.query, isPresented: $board.searchFocused, placement: .toolbar, prompt: "Suchen")
        .onSubmit(of: .search) { board.playFirstVisible(inLobby: false) }
        .toolbar { soundsToolbar }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: tileSize.minWidth), spacing: 14)], spacing: 14) {
                ForEach(board.visible) { sound in
                    PadView(sound: sound,
                            bind: keys.binds[sound.id],
                            hotKey: board.hotKeyState(for: sound),
                            progress: player.progress[sound.id],
                            image: images.images[sound.id],
                            dropTarget: dropTile == sound.id,
                            size: tileSize,
                            onPlay: { board.play(sound) },
                            onEditKey: { board.startRecording(sound) })
                    .background(GeometryReader { geo in
                        Color.clear.preference(key: TileFrameKey.self, value: [sound.id: geo.frame(in: .named("drop"))])
                    })
                    .contextMenu {
                        Button("Abspielen", systemImage: "play") { board.play(sound) }
                        Button("Taste festlegen …", systemImage: "keyboard") { board.startRecording(sound) }
                        Divider()
                        Button(images.images[sound.id] == nil ? "Bild hinzufügen …" : "Bild ändern …", systemImage: "photo") { board.chooseImage(for: sound) }
                        Button("Bild aus der Zwischenablage", systemImage: "doc.on.clipboard") { board.pasteImage(for: sound) }
                        if images.images[sound.id] != nil {
                            Button("Bild entfernen", systemImage: "xmark.circle") { images.remove(sound.id) }
                        }
                        Divider()
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
            .padding(.horizontal, 22)
            .padding(.vertical, 18)
            .id(board.hotKeyRevision)
            .animation(.spring(response: 0.4, dampingFraction: 0.85), value: tileSize)
        }
        .softScrollEdges()
    }

    @ToolbarContentBuilder private var soundsToolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Button("Importieren", systemImage: "plus", action: board.openImportPanel)
                .help("Sound-Dateien oder einen Ordner importieren (⌘O)")

            Menu("Darstellung", systemImage: "square.grid.2x2") {
                Picker("Kachelgröße", selection: $tileSize) {
                    ForEach(TileSize.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.inline)
                Divider()
                Button("Sound-Ordner im Finder zeigen", systemImage: "folder") { library.revealInFinder() }
            }
            .help("Kachelgröße und Sound-Ordner")
        }
    }

    // MARK: Taste aufnehmen

    private func recorder(for sound: Sound) -> some View {
        let bind = keys.binds[sound.id]
        return ZStack {
            Color.black.opacity(0.4).ignoresSafeArea()
                .onTapGesture { board.stopRecording() }
            VStack(spacing: 18) {
                IconTile(systemImage: "keyboard.fill", color: PadView.tint(for: sound.id), size: 54)
                VStack(spacing: 4) {
                    Text("Taste für „\(sound.title)“")
                        .font(.title3.weight(.semibold))
                        .multilineTextAlignment(.center)
                    Text("Drück eine Taste oder Kombination.")
                        .foregroundStyle(.secondary)
                }
                KeyCap(text: bind?.display ?? "–")
                Label("Mit ⌃ (ctrl) oder ⌥ (alt) geht sie überall, auch wenn ClipSound im Hintergrund ist – z. B. in Spielen oder Discord.",
                      systemImage: "globe")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.leading)
                    .fixedSize(horizontal: false, vertical: true)
                if let note = board.recorderNote {
                    Text(note)
                        .font(.callout)
                        .foregroundStyle(.orange)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    Button("Taste entfernen", action: board.removeBind)
                        .glassButton()
                        .disabled(bind == nil)
                        .help("⌫")
                    Spacer()
                    Button("Fertig", action: board.stopRecording)
                        .glassButton(prominent: true)
                        .keyboardShortcut(.defaultAction)
                        .help("Esc")
                }
                .controlSize(.large)
            }
            .padding(28)
            .frame(width: 380)
            // Mattglas unter dem Glas, damit der viele Text auf den bunten Kacheln lesbar bleibt
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 34, style: .continuous))
            .glassSurface(RoundedRectangle(cornerRadius: 34, style: .continuous))
            .transition(.scale(scale: 0.92).combined(with: .opacity))
        }
    }

    private var dropLabel: String {
        guard let id = dropTile, let sound = library.sounds.first(where: { $0.id == id }) else { return "Loslassen zum Importieren" }
        return "Bild auf „\(sound.title)“ · Sounds in die Bibliothek"
    }

    private var dropHighlight: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 26, style: .continuous)
                .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 3, dash: [10, 7]))
                .background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 26, style: .continuous))
                .padding(10)
            Label(dropLabel, systemImage: dropTile == nil ? "square.and.arrow.down.fill" : "photo.fill")
                .font(.title3.weight(.semibold))
                .padding(.horizontal, 22)
                .padding(.vertical, 14)
                .glassSurface(Capsule())
        }
        .allowsHitTesting(false)
    }

    // MARK: Update

    @ViewBuilder private var updateBanner: some View {
        if let release = updater.available, !updateDismissed || updater.state == .downloading {
            HStack(spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 1) {
                    Text("ClipSound \(release.version) ist da").font(.headline)
                    Text("Du hast \(updater.currentVersion).").font(.caption).foregroundStyle(.secondary)
                }
                Spacer(minLength: 12)
                if updater.state == .downloading {
                    ProgressView().controlSize(.small)
                    Text("Wird installiert …").foregroundStyle(.secondary)
                } else {
                    Button("Was ist neu?") { updater.openReleasePage() }
                        .buttonStyle(.link)
                    Button("Später") { updateDismissed = true }
                        .glassButton()
                    Button("Jetzt aktualisieren") { Task { await updater.install() } }
                        .glassButton(prominent: true)
                }
            }
            .padding(.leading, 12)
            .padding(.trailing, 14)
            .padding(.vertical, 10)
            .glassSurface(RoundedRectangle(cornerRadius: 24, style: .continuous))
            .padding(.horizontal, 16)
            .padding(.top, 8)
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

    private var requestTitle: String {
        guard let r = board.lobby.requests.first else { return "" }
        return r.kind == .asked ? "\(r.by) möchte „\(r.soundTitle)“ behalten" : "\(r.by) schenkt dir „\(r.soundTitle)“"
    }

    private var requestShown: Binding<Bool> {
        Binding(get: { !board.lobby.requests.isEmpty }, set: { _ in })
    }

    private var lobbyProblemShown: Binding<Bool> {
        Binding(get: { board.lobby.problem != nil && page != .lobby }, set: { if !$0 { board.lobby.problem = nil } })
    }

    private var problemShown: Binding<Bool> {
        Binding(get: { board.problem != nil }, set: { if !$0 { board.problem = nil } })
    }

    private func tile(at point: CGPoint) -> String? {
        tileFrames.first { $0.value.contains(point) }?.key
    }

    /// Hineingezogen: Sound-Dateien werden importiert, ein Bild auf einer Kachel wird ihr Bild
    private func handleDrop(_ providers: [NSItemProvider], onTile tileID: String?) {
        let group = DispatchGroup()
        var urls: [URL] = []
        var pictures: [NSImage] = [] // Bilder ohne Datei, z. B. aus dem Browser
        let lock = NSLock()
        for provider in providers {
            group.enter()
            if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
                _ = provider.loadObject(ofClass: URL.self) { url, _ in
                    if let url { lock.lock(); urls.append(url); lock.unlock() }
                    group.leave()
                }
            } else if provider.canLoadObject(ofClass: NSImage.self) {
                _ = provider.loadObject(ofClass: NSImage.self) { image, _ in
                    if let image = image as? NSImage { lock.lock(); pictures.append(image); lock.unlock() }
                    group.leave()
                }
            } else {
                group.leave()
            }
        }
        group.notify(queue: .main) {
            let isImage = { (url: URL) in UTType(filenameExtension: url.pathExtension)?.conforms(to: .image) == true }
            let sounds = urls.filter { !isImage($0) }
            if !sounds.isEmpty { board.importItems(sounds) }
            guard let picture = urls.first(where: isImage).flatMap(NSImage.init(contentsOf:)) ?? pictures.first else { return }
            if let id = tileID, library.sounds.contains(where: { $0.id == id }) {
                if !images.set(picture, for: id) { board.problem = "Das Bild konnte nicht übernommen werden." }
            } else if sounds.isEmpty {
                board.problem = "Zieh das Bild direkt auf einen Sound."
            }
        }
    }
}

/// Rahmen der Sound-Kacheln, damit ein hineingezogenes Bild auf der richtigen Kachel landet
struct TileFrameKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { $1 }
    }
}

/// Ziehen ins Fenster: merkt sich die Kachel unter dem Mauszeiger
struct SoundDrop: DropDelegate {
    @Binding var targeted: Bool
    @Binding var tile: String?
    let tileAt: (CGPoint) -> String?
    let perform: ([NSItemProvider], String?) -> Void

    private func track(_ info: DropInfo) {
        let id = info.hasItemsConforming(to: [.image, .fileURL]) ? tileAt(info.location) : nil
        if id != tile { tile = id }
    }

    func dropEntered(info: DropInfo) { targeted = true; track(info) }
    func dropUpdated(info: DropInfo) -> DropProposal? { track(info); return DropProposal(operation: .copy) }
    func dropExited(info: DropInfo) { targeted = false; tile = nil }

    func performDrop(info: DropInfo) -> Bool {
        let id = tileAt(info.location)
        targeted = false
        tile = nil
        perform(info.itemProviders(for: [.fileURL, .image]), id)
        return true
    }
}

// MARK: - Seitenleiste

struct Sidebar: View {
    @Binding var page: Page?
    @ObservedObject var library: SoundLibrary
    @ObservedObject var lobby: Lobby
    @ObservedObject var account: Account

    var body: some View {
        List(selection: $page) {
            Section("Bibliothek") {
                Label("Sounds", systemImage: "waveform")
                    .badge(library.sounds.count)
                    .tag(Page.sounds)
            }
            Section("Zusammen") {
                Label("Lobby", systemImage: lobby.active ? "person.2.wave.2.fill" : "person.2.wave.2")
                    .badge(lobby.active ? Text(lobby.code).monospaced() : nil)
                    .tag(Page.lobby)
            }
            Section("Clans") {
                ForEach(account.clans) { clan in
                    Label {
                        Text(clan.name)
                    } icon: {
                        ClanIcon(name: clan.name, key: clan.id, size: 18)
                    }
                    .tag(Page.clan(clan.id))
                }
                Button { page = .account } label: {
                    Label(account.loggedIn ? "Clan hinzufügen …" : "Anmelden für Clans …", systemImage: "plus.circle")
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
        }
        .navigationSplitViewColumnWidth(min: 200, ideal: 230, max: 320)
        .safeAreaInset(edge: .bottom, spacing: 0) { accountButton }
    }

    /// Konto unten in der Seitenleiste (wie im App Store)
    private var accountButton: some View {
        Button { page = .account } label: {
            HStack(spacing: 10) {
                if let user = account.user {
                    Avatar(name: user.name, size: 32)
                } else {
                    Image(systemName: "person.crop.circle.fill")
                        .font(.system(size: 30))
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(.secondary)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(account.user?.name ?? "Anmelden")
                        .font(.body.weight(.semibold))
                        .lineLimit(1)
                    Text(account.loggedIn ? "Konto & Clans" : "Für Clans mit Freunden")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(8)
            .background(page == .account ? AnyShapeStyle(.fill.tertiary) : AnyShapeStyle(.clear),
                        in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        }
        .buttonStyle(.plain)
        .padding(10)
    }
}

// MARK: - Wiedergabe-Leiste (schwebt unten, Liquid Glass)

/// Lobby, was gerade läuft, Stopp, Überlappen und Lautstärke. Die Glas-Teile fließen ineinander,
/// wenn etwas dazukommt oder verschwindet.
struct PlayerBar: View {
    let board: Board
    @ObservedObject var player: SoundPlayer
    @ObservedObject var lobby: Lobby
    let openLobby: () -> Void
    @Namespace private var glass

    var body: some View {
        GlassGroup(spacing: 10) {
            HStack(spacing: 10) {
                if lobby.active {
                    lobbyChip
                        .glassSurface(Capsule(), interactive: true)
                        .glassID("lobby", in: glass)
                }
                if let channel = player.channels.last {
                    nowPlaying(channel)
                        .glassSurface(Capsule())
                        .glassID("playing", in: glass)
                }
                controls
                    .glassSurface(Capsule())
                    .glassID("controls", in: glass)
            }
        }
        .animation(.spring(response: 0.45, dampingFraction: 0.82), value: player.channels.last?.id)
        .animation(.spring(response: 0.45, dampingFraction: 0.82), value: lobby.active)
    }

    private var lobbyChip: some View {
        Button(action: openLobby) {
            HStack(spacing: 8) {
                Circle()
                    .fill(lobby.phase == .open ? Color.green : Color.orange)
                    .frame(width: 8, height: 8)
                Text(lobby.code)
                    .font(.callout.monospaced().weight(.semibold))
                Label("\(lobby.members.count)", systemImage: "person.2.fill")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .labelStyle(.titleAndIcon)
            }
            .padding(.horizontal, 16)
            .frame(height: 44)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help("Lobby öffnen")
    }

    private func nowPlaying(_ channel: SoundPlayer.Channel) -> some View {
        let tint = Palette.color(for: channel.soundID)
        return HStack(spacing: 10) {
            Group {
                if let image = board.images.images[channel.soundID] {
                    Image(nsImage: image).resizable().scaledToFill()
                } else {
                    Image(systemName: "waveform")
                        .symbolEffect(.variableColor.iterative, isActive: true)
                        .font(.system(size: 13, weight: .bold))
                        .foregroundStyle(.white)
                        .background(Palette.gradient(tint))
                }
            }
            .frame(width: 30, height: 30)
            .background(Palette.gradient(tint))
            .clipShape(Circle())
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 6) {
                    Text(channel.title)
                        .font(.callout.weight(.semibold))
                        .lineLimit(1)
                    if player.channels.count > 1 {
                        Text("+\(player.channels.count - 1)")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(.secondary)
                    }
                }
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Capsule().fill(.primary.opacity(0.12))
                        Capsule().fill(tint).frame(width: max(3, geo.size.width * channel.progress))
                    }
                }
                .frame(height: 3)
            }
            .frame(minWidth: 70, maxWidth: 168, alignment: .leading)
        }
        .padding(.leading, 7)
        .padding(.trailing, 16)
        .frame(height: 44)
        .help(player.channels.map(\.title).joined(separator: ", "))
    }

    private var controls: some View {
        let playing = !player.progress.isEmpty
        return HStack(spacing: 2) {
            Button(action: board.stopAll) {
                Image(systemName: "stop.fill")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(playing ? Color.primary : Color.secondary)
                    .frame(width: 34, height: 32)
                    .contentShape(Rectangle())
            }
            .help(lobby.active ? "Bei allen in der Lobby stoppen (Esc)" : "Alles stoppen (Esc)")
            .disabled(!playing && !lobby.active)

            Button { player.overlap.toggle() } label: {
                Image(systemName: "square.stack.3d.up.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(player.overlap ? Color.white : Color.secondary)
                    .frame(width: 34, height: 30)
                    .background(Circle().fill(player.overlap ? Color.accentColor : .clear))
                    .contentShape(Rectangle())
            }
            .help(player.overlap ? "Überlappen ist an: mehrere Sounds gleichzeitig (⌘L)" : "Überlappen: mehrere Sounds gleichzeitig (⌘L)")

            Divider()
                .frame(height: 20)
                .padding(.horizontal, 8)

            VolumeControl(volume: $player.volume, lobby: lobby)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 10)
        .frame(height: 44)
    }
}

// MARK: - Lautstärke

struct VolumeControl: View {
    @Binding var volume: Double
    @ObservedObject var lobby: Lobby
    @State private var showVolumes = false

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "speaker.wave.3.fill", variableValue: min(volume, 1))
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 22)
                .contentShape(Rectangle())
                .onTapGesture(count: 3) { showVolumes = true } // 3× tippen: System-Lautstärke (in der Lobby alle Geräte)
                .popover(isPresented: $showVolumes, arrowEdge: .top) {
                    if lobby.active { VolumeMixer(lobby: lobby) } else { SystemVolumePopover() }
                }
                .onChange(of: lobby.active) { _, active in
                    if active && LaunchArgs.args.contains("--lobby-volumes") { showVolumes = true }
                }
            Slider(value: $volume, in: 0...SoundPlayer.maxVolume)
                .controlSize(.small)
                .frame(minWidth: 60, maxWidth: 120)
            Text(volume, format: .percent.precision(.fractionLength(0)))
                .font(.callout.weight(.medium))
                .monospacedDigit()
                .foregroundStyle(volume > 1 ? Color.orange : Color.secondary)
                .frame(width: 46, alignment: .trailing)
                .contentShape(Rectangle())
                .onTapGesture(count: 3) { showVolumes = true } // auch hier: 3× → System-Lautstärke
        }
        .padding(.trailing, 4)
        .help(volume > 1 ? "Verstärkt – über 100 % kann es übersteuern" : "Lautstärke")
    }
}

/// 3× auf das Lautsprecher-Symbol (ohne Lobby): die echte Lautstärke dieses Macs –
/// nicht die App-Lautstärke. In einer Lobby übernimmt stattdessen der VolumeMixer.
struct SystemVolumePopover: View {
    @State private var value: Double = 0
    @State private var available = true

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("System-Lautstärke").font(.headline)
            if available {
                HStack(spacing: 8) {
                    Image(systemName: "speaker.fill").foregroundStyle(.secondary).font(.caption)
                    Slider(value: $value, in: 0...1)
                        .controlSize(.small)
                    Image(systemName: "speaker.wave.3.fill").foregroundStyle(.secondary).font(.caption)
                    Text("\(Int((value * 100).rounded())) %")
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                        .frame(width: 42, alignment: .trailing)
                }
                Text("Stellt die echten Lautsprecher dieses Macs ein – nicht die App-Lautstärke.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("Dieses Ausgabegerät meldet keine Lautstärke (z. B. manche HDMI-Ausgänge).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(16)
        .frame(width: 300)
        .onAppear {
            if let v = SystemVolume.get() { value = v; available = true } else { available = false }
        }
        .onChange(of: value) { _, new in SystemVolume.set(new) }
    }
}

// MARK: - Kachel

/// Sound-Kachel im Stil der Kurzbefehle-App: Farbverlauf, weißes Symbol, Taste als Glas-Kapsel
struct PadView: View {
    let sound: Sound
    let bind: KeyBind?
    let hotKey: Board.HotKeyState
    let progress: Double?
    var loading = false
    /// Eigenes Bild statt Farbverlauf
    var image: NSImage? = nil
    /// Gerade wird ein Bild über diese Kachel gezogen
    var dropTarget = false
    var size: TileSize = .medium
    let onPlay: () -> Void
    let onEditKey: () -> Void

    @State private var hovering = false

    /// Feste Systemfarbe pro Sound, damit man Kacheln wiedererkennt
    static func tint(for id: String) -> Color { Palette.color(for: id) }

    private var tint: Color { Self.tint(for: sound.id) }

    var body: some View {
        let playing = progress != nil
        let shape = RoundedRectangle(cornerRadius: size.radius, style: .continuous)

        Button(action: onPlay) {
            VStack(alignment: .leading, spacing: 0) {
                Image(systemName: playing ? "speaker.wave.2.fill" : "waveform")
                    .symbolEffect(.variableColor.iterative, isActive: playing)
                    .font(.system(size: size.symbol, weight: .semibold))
                    .foregroundStyle(.white)
                    .shadow(color: .black.opacity(image == nil ? 0 : 0.5), radius: 2, y: 1)
                    .frame(height: size.symbol + 6, alignment: .leading)
                Spacer(minLength: 6)
                Text(sound.title)
                    .font(.system(size: size.font, weight: .semibold))
                    .foregroundStyle(.white)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .shadow(color: .black.opacity(image == nil ? 0.12 : 0.6), radius: image == nil ? 1 : 3, y: 1)
            }
            .padding(size.padding)
            .frame(height: size.height)
            .background { tileBackground }
            .clipShape(shape)
            .overlay(alignment: .bottom) {
                if let progress {
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule().fill(.white.opacity(0.3))
                            Capsule().fill(.white).frame(width: max(4, geo.size.width * progress))
                        }
                    }
                    .frame(height: 4)
                    .padding(.horizontal, size.padding)
                    .padding(.bottom, 6)
                }
            }
            .overlay { shape.strokeBorder(.white.opacity(playing ? 0.65 : 0.12), lineWidth: playing ? 1.5 : 1) }
            .overlay { if dropTarget { shape.strokeBorder(Color.accentColor, lineWidth: 3) } }
            .contentShape(shape)
        }
        .buttonStyle(PadButtonStyle())
        .saturation(loading ? 0.15 : 1)
        .opacity(loading ? 0.55 : 1)
        .shadow(color: playing ? tint.opacity(0.25) : .black.opacity(hovering ? 0.2 : 0.1),
                radius: playing ? 6 : hovering ? 6 : 3, y: hovering ? 3 : 2)
        .overlay(alignment: .topTrailing) {
            if loading { ProgressView().controlSize(.small).padding(12) }
        }
        // Tasten-Badge liegt außerhalb des Kachel-Buttons, damit er eigene Klicks bekommt
        .overlay(alignment: .topTrailing) { keyBadge.padding(size.padding - 3) }
        .scaleEffect(dropTarget ? 1.05 : hovering && !loading ? 1.025 : 1)
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: hovering)
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: dropTarget)
        .animation(.easeOut(duration: 0.2), value: playing)
        .onHover { hovering = $0 }
        .help(sound.id)
    }

    /// Bild (unten abgedunkelt, damit der Titel lesbar bleibt) oder Farbverlauf
    @ViewBuilder private var tileBackground: some View {
        if let image {
            Image(nsImage: image)
                .resizable()
                .scaledToFill()
                .overlay(LinearGradient(colors: [.black.opacity(0.1), .black.opacity(0.35), .black.opacity(0.75)],
                                        startPoint: .top, endPoint: .bottom))
        } else {
            Palette.tileGradient(tint)
        }
    }

    @ViewBuilder private var keyBadge: some View {
        if let bind {
            Button(action: onEditKey) {
                HStack(spacing: 3) {
                    if hotKey == .global {
                        Image(systemName: "globe").font(.system(size: 9, weight: .bold))
                    } else if hotKey == .failed {
                        Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 9)).foregroundStyle(.yellow)
                    }
                    Text(bind.display).font(.system(size: 11, weight: .bold, design: .rounded))
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .glassSurface(Capsule(), clear: true, interactive: true)
            }
            .buttonStyle(.plain)
            .help(badgeHelp)
        } else if hovering {
            Button(action: onEditKey) {
                Image(systemName: "keyboard")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 26, height: 20)
                    .glassSurface(Capsule(), clear: true, interactive: true)
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
            .brightness(configuration.isPressed ? -0.06 : 0)
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
            .animation(.spring(response: 0.2, dampingFraction: 0.6), value: configuration.isPressed)
    }
}

/// Taste wie auf der Tastatur (Aufnahme-Fenster)
struct KeyCap: View {
    let text: String

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        Text(text)
            .font(.system(size: 30, weight: .semibold, design: .rounded))
            .frame(minWidth: 76, minHeight: 64)
            .padding(.horizontal, 16)
            .background {
                shape.fill(Color(nsColor: .controlBackgroundColor))
                    .shadow(color: .black.opacity(0.3), radius: 0, y: 3)
                    .shadow(color: .black.opacity(0.15), radius: 10, y: 5)
            }
            .overlay(shape.strokeBorder(.separator))
            .contentTransition(.numericText())
            .animation(.snappy, value: text)
    }
}
