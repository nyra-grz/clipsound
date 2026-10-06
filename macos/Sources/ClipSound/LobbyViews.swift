import SwiftUI
import AppKit

/// Leiste oben im Fenster, solange man in einer Lobby ist
struct LobbyBar: View {
    @ObservedObject var lobby: Lobby
    @Binding var showLobby: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "person.2.fill")
                    .foregroundStyle(lobby.phase == .open ? Color.accentColor : Color.secondary)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 6) {
                        Text(lobby.isHost ? "Deine Lobby" : "Lobby von \(lobby.hostName)").font(.headline)
                        Text(lobby.code)
                            .font(.system(.headline, design: .monospaced))
                            .textSelection(.enabled)
                    }
                    Text(status).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if lobby.phase != .open { ProgressView().controlSize(.small) }
                Button("Code kopieren") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(lobby.code, forType: .string)
                    lobby.note = "Code \(lobby.code) kopiert"
                }
                Button("Verlassen", role: .destructive) { lobby.leave() }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
            Divider()
        }
        .background(.bar)
    }

    private var status: String {
        switch lobby.phase {
        case .connecting: return "Verbinde …"
        case .reconnecting: return "Verbindung weg – verbinde neu …"
        default: break
        }
        let others = lobby.members.filter { $0.id != lobby.me }.map(\.name)
        var parts = [others.isEmpty ? "Noch niemand da – schick den Code rum" : "Mit " + others.joined(separator: ", ")]
        if lobby.isHost, let u = lobby.uploaded, u.done < u.total { parts.append("Sounds werden freigegeben: \(u.done) von \(u.total)") }
        return parts.joined(separator: " · ")
    }
}

/// Kurze Meldung unten im Fenster, verschwindet von selbst
struct LobbyNote: View {
    @ObservedObject var lobby: Lobby

    var body: some View {
        Group {
            if let note = lobby.note {
                Text(note)
                    .font(.callout)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .background(.regularMaterial, in: Capsule())
                    .shadow(color: .black.opacity(0.15), radius: 8, y: 3)
                    .padding(.bottom, 14)
                    .transition(.opacity.combined(with: .move(edge: .bottom)))
                    .task(id: note) {
                        try? await Task.sleep(nanoseconds: 3_000_000_000)
                        if lobby.note == note { lobby.note = nil }
                    }
            }
        }
        .animation(.easeOut(duration: 0.2), value: lobby.note)
    }
}

/// Lobby öffnen oder mit Code beitreten
struct LobbySheet: View {
    @ObservedObject var lobby: Lobby
    @ObservedObject var library: SoundLibrary
    @Environment(\.dismiss) private var dismiss
    @State private var code = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Zusammen abspielen").font(.title2.bold())
                Text("Jeder Sound, den jemand drückt, läuft bei allen in der Lobby gleichzeitig.")
                    .foregroundStyle(.secondary)
            }

            if lobby.active {
                inLobby
            } else {
                Form {
                    TextField("Dein Name", text: $lobby.name, prompt: Text(Host.current().localizedName ?? "Name"))
                }
                .formStyle(.grouped)
                .frame(height: 70)
                .scrollDisabled(true)

                GroupBox {
                    HStack(alignment: .center, spacing: 12) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Eigene Lobby öffnen").font(.headline)
                            Text(library.sounds.isEmpty
                                 ? "Du hast noch keine Sounds zum Teilen."
                                 : "Deine \(library.sounds.count) Sounds werden für die anderen freigegeben.")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Öffnen") { lobby.open(with: library.sounds) }
                            .buttonStyle(.borderedProminent)
                            .disabled(lobby.phase != .off)
                    }
                    .padding(6)
                }

                GroupBox {
                    HStack(spacing: 12) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Beitreten").font(.headline)
                            Text("Code von dem, der die Lobby geöffnet hat.").font(.callout).foregroundStyle(.secondary)
                        }
                        Spacer()
                        TextField("Code", text: $code, prompt: Text("ABC12"))
                            .font(.system(.title3, design: .monospaced))
                            .multilineTextAlignment(.center)
                            .frame(width: 100)
                            .onChange(of: code) { _, new in
                                let clean = String(new.uppercased().filter { $0.isLetter || $0.isNumber }.prefix(5))
                                if clean != new { code = clean }
                            }
                            .onSubmit(join)
                        Button("Beitreten", action: join)
                            .disabled(code.count != 5 || lobby.phase != .off)
                    }
                    .padding(6)
                }

                if let problem = lobby.problem {
                    Label(problem, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                }
            }

            HStack {
                Spacer()
                Button(lobby.active ? "Fertig" : "Abbrechen") { dismiss() }
                    .keyboardShortcut(lobby.active ? .defaultAction : .cancelAction)
            }
        }
        .padding(22)
        .frame(width: 460)
        .onChange(of: lobby.phase) { old, new in
            // Gast: sobald drin, Blatt zu – die Sounds sieht man im Fenster
            if old != .open && new == .open && !lobby.isHost { dismiss() }
        }
    }

    private func join() {
        guard code.count == 5 else { return }
        lobby.join(code)
    }

    @ViewBuilder private var inLobby: some View {
        GroupBox {
            VStack(spacing: 10) {
                Text(lobby.isHost ? "Dein Lobby-Code" : "Lobby von \(lobby.hostName)")
                    .foregroundStyle(.secondary)
                Text(lobby.code)
                    .font(.system(size: 40, weight: .semibold, design: .monospaced))
                    .kerning(6)
                    .textSelection(.enabled)
                if lobby.phase != .open {
                    ProgressView().controlSize(.small)
                }
                Button("Code kopieren") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(lobby.code, forType: .string)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(10)
        }
        if !lobby.members.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("In der Lobby").font(.headline)
                ForEach(lobby.members) { member in
                    Label {
                        Text(member.name + (member.id == lobby.me ? " (du)" : ""))
                    } icon: {
                        Image(systemName: member.host ? "crown" : "person")
                    }
                }
            }
        }
        if lobby.isHost {
            Text("Rechtsklick auf einen Sound → „Schenken an“, um ihn jemandem zu geben. Gäste können dich auch fragen – du entscheidest.")
                .font(.callout).foregroundStyle(.secondary)
        }
        Button("Lobby verlassen", role: .destructive) { lobby.leave() }
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
