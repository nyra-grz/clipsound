import SwiftUI
import AppKit

/// Kurze Meldung unten im Fenster (über der Wiedergabe-Leiste), verschwindet von selbst
struct LobbyNote: View {
    @ObservedObject var lobby: Lobby

    var body: some View {
        Group {
            if let note = lobby.note {
                Text(note)
                    .font(.callout.weight(.medium))
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .glassSurface(Capsule())
                    .transition(.opacity.combined(with: .move(edge: .bottom)).combined(with: .scale(scale: 0.9)))
                    .task(id: note) {
                        try? await Task.sleep(nanoseconds: 3_000_000_000)
                        if lobby.note == note { lobby.note = nil }
                    }
            }
        }
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: lobby.note)
    }
}

/// Seite „Lobby“: öffnen oder mit Code beitreten; in der Lobby Code, Leute und (als Gast) die Sounds des Hosts
struct LobbyPage: View {
    @ObservedObject var board: Board
    @ObservedObject var lobby: Lobby
    @ObservedObject var library: SoundLibrary
    @ObservedObject var player: SoundPlayer
    let tileSize: TileSize
    @State private var code = ""

    var body: some View {
        Group {
            if !lobby.active {
                setup
            } else if lobby.isHost {
                ScrollView { inLobby.padding(.horizontal, 22).padding(.vertical, 18) }
                    .softScrollEdges()
            } else {
                // Gast: Sounds des Hosts, mit Suche
                ScrollView { inLobby.padding(.horizontal, 22).padding(.vertical, 18) }
                    .softScrollEdges()
                    .searchable(text: $board.query, isPresented: $board.searchFocused, placement: .toolbar, prompt: "Suchen")
                    .onSubmit(of: .search) { board.playFirstVisible(inLobby: true) }
            }
        }
        .navigationTitle("Lobby")
        .navigationSubtitle(subtitle)
    }

    private var subtitle: String {
        guard lobby.active else { return "Zusammen abspielen" }
        let n = lobby.members.count
        return n == 1 ? "1 Person" : "\(n) Personen"
    }

    private func copyCode() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(lobby.code, forType: .string)
        lobby.note = "Code \(lobby.code) kopiert"
    }

    private func join() {
        guard code.count == 5 else { return }
        lobby.join(code)
    }

    // MARK: Noch nicht in einer Lobby

    private var setup: some View {
        ScrollView {
            VStack(spacing: 26) {
                VStack(spacing: 12) {
                    IconTile(systemImage: "person.2.wave.2.fill", color: .indigo, size: 84)
                    Text("Zusammen abspielen")
                        .font(.largeTitle.weight(.bold))
                    Text("Jeder Sound, den jemand drückt, läuft bei allen in der Lobby gleichzeitig. Ein Konto braucht man dafür nicht.")
                        .font(.title3)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 480)
                }
                .padding(.top, 12)

                HStack(spacing: 12) {
                    Image(systemName: "person.crop.circle.fill")
                        .font(.title2)
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(.secondary)
                    TextField("Dein Name", text: $lobby.name, prompt: Text(Host.current().localizedName ?? "Dein Name"))
                        .textFieldStyle(.plain)
                        .font(.title3)
                    Text("So sehen dich die anderen")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
                .card(padding: 16)

                HStack(alignment: .top, spacing: 16) {
                    openCard
                    joinCard
                }
                .fixedSize(horizontal: false, vertical: true)

                if let problem = lobby.problem {
                    Label(problem, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                }

                VStack(spacing: 4) {
                    Label("Der Server speichert keine Sounds – sie gehen direkt von Gerät zu Gerät.", systemImage: "lock.shield.fill")
                    Link("Datenschutzerklärung", destination: Account.privacyURL)
                }
                .font(.callout)
                .foregroundStyle(.secondary)
            }
            .frame(maxWidth: 600)
            .padding(32)
            .frame(maxWidth: .infinity)
        }
        .softScrollEdges()
    }

    private var openCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            IconTile(systemImage: "dot.radiowaves.left.and.right", color: .orange, size: 42)
            Text("Eigene Lobby öffnen")
                .font(.title3.weight(.semibold))
            Text(library.sounds.isEmpty
                 ? "Du hast noch keine Sounds zum Teilen."
                 : "Deine \(library.sounds.count) Sounds werden für die anderen freigegeben.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            Button { lobby.open(with: library.sounds) } label: {
                Text("Lobby öffnen").frame(maxWidth: .infinity)
            }
            .glassButton(prominent: true)
            .controlSize(.large)
            .disabled(lobby.phase != .off)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .card(padding: 20)
    }

    private var joinCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            IconTile(systemImage: "number", color: .blue, size: 42)
            Text("Mit Code beitreten")
                .font(.title3.weight(.semibold))
            TextField("Code", text: $code, prompt: Text("ABC12"))
                .textFieldStyle(.plain)
                .font(.system(size: 26, weight: .semibold, design: .monospaced))
                .kerning(6)
                .multilineTextAlignment(.center)
                .padding(.vertical, 8)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.background.opacity(0.7)))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.separator))
                .onChange(of: code) { _, new in
                    let clean = String(new.uppercased().filter { $0.isLetter || $0.isNumber }.prefix(5))
                    if clean != new { code = clean }
                }
                .onSubmit(join)
            Spacer(minLength: 4)
            Button(action: join) {
                Text("Beitreten").frame(maxWidth: .infinity)
            }
            .glassButton()
            .controlSize(.large)
            .disabled(code.count != 5 || lobby.phase != .off)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .card(padding: 20)
    }

    // MARK: In der Lobby

    private var inLobby: some View {
        VStack(alignment: .leading, spacing: 24) {
            header
            members
            if lobby.isHost { hostInfo } else { guestSounds }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 14) {
                IconTile(systemImage: "person.2.wave.2.fill", color: .indigo, size: 52)
                VStack(alignment: .leading, spacing: 4) {
                    Text(lobby.isHost ? "Deine Lobby" : "Lobby von \(lobby.hostName)")
                        .font(.title2.weight(.bold))
                    HStack(spacing: 6) {
                        if lobby.phase == .open {
                            Circle().fill(.green).frame(width: 8, height: 8)
                        } else {
                            ProgressView().controlSize(.mini)
                        }
                        Text(status)
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                    }
                }
            }
            HStack(alignment: .center, spacing: 16) {
                CodeBoxes(code: lobby.code)
                Spacer(minLength: 0)
                VStack(alignment: .trailing, spacing: 8) {
                    Button("Code kopieren", systemImage: "doc.on.doc", action: copyCode)
                        .glassButton()
                    Button("Verlassen", systemImage: "rectangle.portrait.and.arrow.right", role: .destructive) { lobby.leave() }
                        .glassButton()
                        .tint(.red)
                }
                .controlSize(.large)
            }
        }
        .card(padding: 20)
    }

    private var status: String {
        switch lobby.phase {
        case .connecting: return "Verbinde …"
        case .reconnecting: return "Verbindung weg – verbinde neu …"
        default: break
        }
        let others = lobby.members.filter { $0.id != lobby.me }.map(\.name)
        return others.isEmpty ? "Noch niemand da – schick den Code rum" : "Mit " + others.joined(separator: ", ")
    }

    private var members: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("In der Lobby").font(.headline)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 250), spacing: 10, alignment: .leading)], alignment: .leading, spacing: 10) {
                ForEach(lobby.members) { member in
                    HStack(spacing: 10) {
                        Avatar(name: member.name, size: 34)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(member.name + (member.id == lobby.me ? " (du)" : ""))
                                .font(.body.weight(.medium))
                                .lineLimit(1)
                            Text(member.host ? "Hat die Lobby geöffnet" : "Hört mit")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 0)
                        if member.host {
                            Image(systemName: "crown.fill").foregroundStyle(.orange)
                        }
                    }
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(.quinary))
                }
            }
        }
    }

    @ViewBuilder private var hostInfo: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let u = lobby.uploaded, u.done < u.total {
                ProgressView(value: Double(u.done), total: Double(max(u.total, 1))) {
                    Text("Sounds werden freigegeben")
                } currentValueLabel: {
                    Text("\(u.done) von \(u.total)")
                }
            }
            Label("Rechtsklick auf einen Sound → „Schenken an“, um ihn jemandem zu geben. Gäste können dich auch fragen – du entscheidest.",
                  systemImage: "gift.fill")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .card()
    }

    @ViewBuilder private var guestSounds: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(lobby.hostName.isEmpty ? "Sounds" : "Sounds von \(lobby.hostName)").font(.headline)
                Spacer()
                Text("\(lobby.sounds.count)").foregroundStyle(.secondary).monospacedDigit()
            }
            if lobby.sounds.isEmpty {
                ContentUnavailableView {
                    Label(lobby.phase == .open ? "Noch keine Sounds" : "Verbinde …", systemImage: "waveform")
                } description: {
                    Text(lobby.phase == .open ? "\(lobby.hostName) hat noch keine Sounds freigegeben." : "Einen Moment.")
                }
                .frame(height: 240)
            } else if board.visibleLobby.isEmpty {
                ContentUnavailableView.search(text: board.query)
                    .frame(height: 240)
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: tileSize.minWidth), spacing: 14)], spacing: 14) {
                    ForEach(board.visibleLobby) { sound in
                        PadView(sound: Sound(url: URL(fileURLWithPath: sound.name)),
                                bind: nil,
                                hotKey: .local,
                                progress: sound.local.flatMap { player.progress[$0.id] },
                                loading: sound.local == nil,
                                size: tileSize,
                                onPlay: { board.play(sound) },
                                onEditKey: {})
                        .contextMenu {
                            Button("Abspielen", systemImage: "play") { board.play(sound) }
                            Button("Behalten …", systemImage: "square.and.arrow.down") { board.lobby.ask(sound) }
                        }
                    }
                }
            }
        }
    }
}

/// Lobby-Code als einzelne Felder, wie ein Bestätigungscode
struct CodeBoxes: View {
    let code: String

    var body: some View {
        HStack(spacing: 8) {
            ForEach(Array(code.enumerated()), id: \.offset) { _, char in
                Text(String(char))
                    .font(.system(size: 30, weight: .semibold, design: .monospaced))
                    .frame(width: 46, height: 58)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.background))
                    .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.separator))
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Code \(code)")
    }
}

/// Versteckt: in einer Lobby dreimal auf das Lautsprecher-Symbol klicken. Jeder stellt hier die echten Lautsprecher
/// jedes Geräts ein.
struct VolumeMixer: View {
    @ObservedObject var lobby: Lobby

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Lautstärke der Geräte").font(.headline)
            ForEach(lobby.members) { member in
                VolumeRow(lobby: lobby, member: member)
            }
            Text("Stellt die echten Lautsprecher ein.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(16)
        .frame(width: 320)
    }
}

private struct VolumeRow: View {
    @ObservedObject var lobby: Lobby
    let member: LobbyMember
    @State private var value: Double = 0
    @State private var editing = false

    private var isMe: Bool { member.id == lobby.me }
    private var canEdit: Bool { member.volume != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Label(member.name + (isMe ? " (du)" : ""), systemImage: member.host ? "crown" : "person")
                Spacer()
                Text(member.volume == nil ? "–" : "\(Int((value * 100).rounded())) %")
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
            if member.volume != nil {
                HStack(spacing: 6) {
                    Image(systemName: "speaker.fill").foregroundStyle(.secondary).font(.caption)
                    Slider(value: $value, in: 0...1) { editing = $0 }
                        .controlSize(.small)
                        .disabled(!canEdit)
                    Image(systemName: "speaker.wave.3.fill").foregroundStyle(.secondary).font(.caption)
                }
                if member.system == false {
                    Text("Nur die App – im Browser geht es nicht anders.").font(.caption2).foregroundStyle(.secondary)
                }
            } else {
                Text("Dieses Gerät meldet keine Lautstärke.").font(.caption).foregroundStyle(.secondary)
            }
        }
        .onAppear { value = member.volume ?? 0 }
        .onChange(of: member.volume) { _, new in if !editing, let new { value = new } }
        .onChange(of: value) { _, new in
            guard editing else { return }
            if isMe { lobby.setOwnVolume(new) } else { lobby.setVolume(of: member, to: new) }
        }
    }
}
