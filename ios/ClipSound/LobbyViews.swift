import SwiftUI
import UIKit

/// Leiste oben, solange man in einer Lobby ist
struct LobbyBar: View {
    @ObservedObject var lobby: Lobby
    let open: () -> Void

    var body: some View {
        Button(action: open) {
            HStack(spacing: 10) {
                Image(systemName: "person.2.fill")
                    .foregroundStyle(lobby.phase == .open ? Color.accentColor : Color.secondary)
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 6) {
                        Text(lobby.isHost ? "Deine Lobby" : "Lobby von \(lobby.hostName)")
                            .font(.subheadline.weight(.semibold))
                            .lineLimit(1)
                        Text(lobby.code).font(.subheadline.monospaced().weight(.semibold))
                    }
                    Text(status).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                if lobby.phase != .open { ProgressView() }
                Image(systemName: "chevron.right").font(.caption.weight(.semibold)).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .background(.bar)
        .overlay(alignment: .bottom) { Divider() }
    }

    private var status: String {
        switch lobby.phase {
        case .connecting: return "Verbinde …"
        case .reconnecting: return "Verbindung weg – verbinde neu …"
        default: break
        }
        let others = lobby.members.filter { $0.id != lobby.me }.map(\.name)
        if lobby.isHost, let u = lobby.uploaded, u.done < u.total { return "Sounds werden freigegeben: \(u.done) von \(u.total)" }
        return others.isEmpty ? "Noch niemand da – schick den Code rum" : "Mit " + others.joined(separator: ", ")
    }
}

/// Kurze Meldung unten, verschwindet von selbst
struct LobbyNote: View {
    @ObservedObject var lobby: Lobby

    var body: some View {
        Group {
            if let note = lobby.note {
                Text(note)
                    .font(.footnote)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 9)
                    .background(.regularMaterial, in: Capsule())
                    .shadow(color: .black.opacity(0.15), radius: 8, y: 3)
                    .padding(.horizontal, 24)
                    .padding(.bottom, 8)
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
    @FocusState private var codeFocused: Bool

    var body: some View {
        NavigationStack {
            Form {
                if lobby.active {
                    inLobby
                } else {
                    Section {
                        TextField("Dein Name", text: $lobby.name)
                            .textContentType(.nickname)
                    } footer: {
                        Text("Jeder Sound, den jemand drückt, läuft bei allen in der Lobby gleichzeitig.")
                    }

                    Section {
                        Button {
                            lobby.open(with: library.sounds)
                        } label: {
                            Label("Eigene Lobby öffnen", systemImage: "person.2.badge.plus")
                        }
                        .disabled(lobby.phase != .off)
                    } footer: {
                        Text(library.sounds.isEmpty
                             ? "Du hast noch keine Sounds zum Teilen."
                             : "Deine \(library.sounds.count) Sounds werden für die anderen freigegeben.")
                    }

                    Section("Beitreten") {
                        HStack {
                            TextField("Code", text: $code, prompt: Text("ABC12"))
                                .font(.title3.monospaced())
                                .textInputAutocapitalization(.characters)
                                .autocorrectionDisabled()
                                .focused($codeFocused)
                                .submitLabel(.join)
                                .onSubmit(join)
                                .onChange(of: code) { _, new in
                                    let clean = String(new.uppercased().filter { $0.isLetter || $0.isNumber }.prefix(5))
                                    if clean != new { code = clean }
                                }
                            Button("Beitreten", action: join)
                                .buttonStyle(.borderedProminent)
                                .disabled(code.count != 5 || lobby.phase != .off)
                        }
                    }

                    if let problem = lobby.problem {
                        Section {
                            Label(problem, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        }
                    }
                }
            }
            .navigationTitle("Zusammen abspielen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
        }
        .onChange(of: lobby.phase) { old, new in
            // Gast: sobald drin, Blatt zu – die Sounds sieht man im Hauptfenster
            if old != .open && new == .open && !lobby.isHost { dismiss() }
        }
    }

    private func join() {
        guard code.count == 5 else { return }
        codeFocused = false
        lobby.join(code)
    }

    @ViewBuilder private var inLobby: some View {
        Section {
            VStack(spacing: 8) {
                Text(lobby.isHost ? "Dein Lobby-Code" : "Lobby von \(lobby.hostName)")
                    .foregroundStyle(.secondary)
                Text(lobby.code)
                    .font(.system(size: 44, weight: .semibold, design: .monospaced))
                    .kerning(6)
                    .textSelection(.enabled)
                if lobby.phase != .open { ProgressView() }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)
            ShareLink(item: "Komm in meine ClipSound-Lobby: \(lobby.code)") {
                Label("Code teilen", systemImage: "square.and.arrow.up")
            }
            Button {
                UIPasteboard.general.string = lobby.code
                lobby.note = "Code \(lobby.code) kopiert"
            } label: {
                Label("Code kopieren", systemImage: "doc.on.doc")
            }
        }
        Section("In der Lobby") {
            ForEach(lobby.members) { member in
                Label {
                    Text(member.name + (member.id == lobby.me ? " (du)" : ""))
                } icon: {
                    Image(systemName: member.host ? "crown" : "person")
                }
            }
        }
        if lobby.isHost {
            Section {
                EmptyView()
            } footer: {
                Text("Halte einen Sound gedrückt → „Schenken an“, um ihn jemandem zu geben. Gäste können dich auch fragen – du entscheidest.")
            }
        }
        Section {
            Button("Lobby verlassen", role: .destructive) { lobby.leave() }
        }
    }
}
