import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @ObservedObject var library: SoundLibrary
    @ObservedObject var player: SoundPlayer
    @ObservedObject var lobby: Lobby
    @State private var showLobby = false

    @State private var query = ""
    @State private var importing = false
    @State private var pendingDelete: Sound?
    @State private var problem: String?
    @State private var tapCount = 0

    /// Gast in einer fremden Lobby: dann zeigt die App die Sounds des Hosts
    private var inGuestLobby: Bool { lobby.active && !lobby.isHost }

    private var visibleLobby: [LobbySound] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? lobby.sounds : lobby.sounds.filter { $0.title.lowercased().contains(q) }
    }

    private func play(_ sound: Sound) {
        tapCount += 1
        // Als Host läuft alles über die Lobby, damit es bei allen gleichzeitig startet
        if lobby.active && lobby.isHost {
            if lobby.play(own: sound) { return }
            lobby.note = "„\(sound.title)“ ist noch nicht in der Lobby – nur bei dir abgespielt."
        }
        if !player.play(sound) { problem = "„\(sound.title)“ konnte nicht abgespielt werden." }
    }

    private func stopAll() {
        player.stopAll()
        if lobby.active { lobby.stopAll() }
    }

    private var visible: [Sound] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? library.sounds : library.sounds.filter { $0.id.lowercased().contains(q) || $0.title.lowercased().contains(q) }
    }

    var body: some View {
        NavigationStack {
            Group {
                if inGuestLobby {
                    lobbyGrid
                } else if library.sounds.isEmpty {
                    ContentUnavailableView {
                        Label("Keine Sounds", systemImage: "waveform")
                    } description: {
                        Text("Importiere Sounds aus der Dateien-App – einzeln oder als ganzen Ordner.")
                    } actions: {
                        Button("Sounds importieren") { importing = true }
                            .buttonStyle(.borderedProminent)
                    }
                } else if visible.isEmpty {
                    ContentUnavailableView.search(text: query)
                } else {
                    grid
                }
            }
            .navigationTitle(inGuestLobby ? "Lobby" : "ClipSound")
            .searchable(text: $query, prompt: "Suchen")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Alles stoppen", systemImage: "stop.fill", action: stopAll)
                        .disabled(player.progress.isEmpty && !lobby.active)
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Lobby", systemImage: lobby.active ? "person.2.fill" : "person.2") { showLobby = true }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Importieren", systemImage: "plus") { importing = true }
                }
            }
            .safeAreaInset(edge: .top, spacing: 0) {
                if lobby.active { LobbyBar(lobby: lobby) { showLobby = true } }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) {
                VStack(spacing: 0) {
                    LobbyNote(lobby: lobby)
                    controls
                }
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.audio, .folder], allowsMultipleSelection: true) { result in
            switch result {
            case .success(let urls):
                let r = library.importItems(urls)
                if !r.rejected.isEmpty {
                    problem = "Keine Sounds, übersprungen:\n" + r.rejected.prefix(5).joined(separator: "\n")
                }
            case .failure(let error):
                problem = error.localizedDescription
            }
        }
        .confirmationDialog("„\(pendingDelete?.title ?? "")“ löschen?", isPresented: deleteShown, titleVisibility: .visible, presenting: pendingDelete) { sound in
            Button("Löschen", role: .destructive) { library.delete(sound) }
        }
        .alert("ClipSound", isPresented: problemShown, presenting: problem) { _ in
            Button("OK") {}
        } message: { Text($0) }
        .sheet(isPresented: $showLobby) { LobbySheet(lobby: lobby, library: library) }
        .alert(requestTitle, isPresented: requestShown, presenting: lobby.requests.first) { request in
            Button(request.kind == .asked ? "Erlauben" : "Annehmen") { lobby.answer(request, ok: true) }
            Button("Ablehnen", role: .cancel) { lobby.answer(request, ok: false) }
        } message: { request in
            Text(request.kind == .asked
                 ? "Der Sound wird bei \(request.by) gespeichert."
                 : "Der Sound kommt in deine Sounds.")
        }
        .alert("Lobby", isPresented: lobbyProblemShown, presenting: lobby.problem) { _ in
            Button("OK") {}
        } message: { Text($0) }
        .sensoryFeedback(.impact(weight: .light), trigger: tapCount)
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 10)], spacing: 10) {
                ForEach(visible) { sound in
                    Tile(sound: sound, progress: player.progress[sound.id]) { play(sound) }
                    .contextMenu {
                        Button("Abspielen", systemImage: "play") { play(sound) }
                        if lobby.isHost && !lobby.guests.isEmpty {
                            Menu("Schenken an", systemImage: "gift") {
                                ForEach(lobby.guests) { member in
                                    Button(member.name) { lobby.offer(sound, to: member) }
                                }
                            }
                        }
                        Button("Löschen", systemImage: "trash", role: .destructive) { pendingDelete = sound }
                    }
                }
            }
            .padding(16)
        }
        .background(Color(.systemGroupedBackground))
    }

    /// Lautstärke und Überlappen unten
    private var controls: some View {
        HStack(spacing: 12) {
            Image(systemName: "speaker.wave.2.fill", variableValue: min(player.volume, 1))
                .foregroundStyle(.secondary)
                .frame(width: 24)
            Slider(value: $player.volume, in: 0...SoundPlayer.maxVolume)
            Text(player.volume, format: .percent.precision(.fractionLength(0)))
                .monospacedDigit()
                .foregroundStyle(player.volume > 1 ? Color.orange : Color.secondary)
                .frame(width: 48, alignment: .trailing)
                .onTapGesture { player.volume = 1 }
            Toggle(isOn: $player.overlap) {
                Image(systemName: "square.stack.3d.up")
            }
            .toggleStyle(.button)
            .accessibilityLabel("Überlappen")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(.bar)
    }

    @ViewBuilder private var lobbyGrid: some View {
        if lobby.sounds.isEmpty {
            ContentUnavailableView {
                Label(lobby.phase == .open ? "Noch keine Sounds" : "Verbinde …", systemImage: "person.2")
            } description: {
                Text(lobby.phase == .open ? "\(lobby.hostName) hat noch keine Sounds freigegeben." : "Einen Moment.")
            }
        } else if visibleLobby.isEmpty {
            ContentUnavailableView.search(text: query)
        } else {
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 10)], spacing: 10) {
                    ForEach(visibleLobby) { sound in
                        Tile(sound: Sound(url: URL(fileURLWithPath: sound.name)),
                             progress: sound.local.flatMap { player.progress[$0.id] }) {
                            tapCount += 1
                            lobby.play(sound)
                        }
                        .opacity(sound.local == nil ? 0.5 : 1)
                        .overlay(alignment: .topTrailing) { if sound.local == nil { ProgressView().padding(10) } }
                        .contextMenu {
                            Button("Abspielen", systemImage: "play") { lobby.play(sound) }
                            Button("Behalten …", systemImage: "square.and.arrow.down") { lobby.ask(sound) }
                        }
                    }
                }
                .padding(16)
            }
            .background(Color(.systemGroupedBackground))
        }
    }

    private var requestTitle: String {
        guard let r = lobby.requests.first else { return "" }
        return r.kind == .asked ? "\(r.by) möchte „\(r.soundTitle)“ behalten" : "\(r.by) schenkt dir „\(r.soundTitle)“"
    }

    private var requestShown: Binding<Bool> {
        Binding(get: { !lobby.requests.isEmpty }, set: { _ in })
    }

    private var lobbyProblemShown: Binding<Bool> {
        Binding(get: { lobby.problem != nil && !showLobby }, set: { if !$0 { lobby.problem = nil } })
    }

    private var deleteShown: Binding<Bool> {
        Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } })
    }

    private var problemShown: Binding<Bool> {
        Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })
    }
}

/// Eine Sound-Kachel – gleiche Gestaltung wie in der Mac-App
struct Tile: View {
    let sound: Sound
    let progress: Double?
    let action: () -> Void

    private var tint: Color {
        let colors: [Color] = [.blue, .purple, .pink, .orange, .green, .teal, .indigo, .red]
        var h: UInt32 = 0
        for unit in sound.id.utf16 { h = h &* 31 &+ UInt32(unit) }
        return colors[Int(h % UInt32(colors.count))]
    }

    var body: some View {
        let playing = progress != nil
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)

        Button(action: action) {
            VStack(alignment: .leading, spacing: 0) {
                Image(systemName: playing ? "speaker.wave.2.fill" : "waveform")
                    .symbolEffect(.variableColor.iterative, isActive: playing)
                    .font(.system(size: 17, weight: .medium))
                    .foregroundStyle(tint)
                    .frame(height: 22, alignment: .leading)
                Spacer(minLength: 6)
                Text(sound.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(12)
            .frame(height: 92)
            .background {
                shape.fill(playing ? AnyShapeStyle(tint.opacity(0.16)) : AnyShapeStyle(Color(.secondarySystemGroupedBackground)))
            }
            .overlay(alignment: .bottom) {
                if let progress {
                    GeometryReader { geo in
                        Capsule().fill(tint).frame(width: max(4, geo.size.width * progress), height: 3)
                    }
                    .frame(height: 3)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 5)
                }
            }
            .overlay { shape.strokeBorder(playing ? tint.opacity(0.7) : Color.primary.opacity(0.06), lineWidth: 1) }
            .contentShape(shape)
        }
        .buttonStyle(.plain)
    }
}
