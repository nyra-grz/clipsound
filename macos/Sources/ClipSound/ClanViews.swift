import SwiftUI
import AppKit


// MARK: - Seite

struct ClanPage: View {
    @StateObject private var room: ClanRoom

    init(account: Account, lobby: Lobby, library: SoundLibrary, clan: ClanSummary) {
        _room = StateObject(wrappedValue: ClanRoom(account: account, lobby: lobby, library: library, clanId: clan.id, name: clan.name))
    }

    var body: some View {
        Form {
            Section {
                LabeledContent("Rolle", value: room.isAdmin ? "Admin" : "Mitglied")
                Button {
                    Task { await room.startSession() }
                } label: {
                    Label(room.sessionCode == nil ? "Clan-Session starten" : "Session beitreten (\(room.sessionCode!))",
                          systemImage: "play.circle.fill")
                }
                .disabled(room.busy)
                if room.lobby.active {
                    Button("Session verlassen", role: .destructive) { room.lobby.leave() }
                }
            } header: {
                Text(room.name)
            } footer: {
                Text("Alle im Clan können der Session beitreten und gemeinsam abspielen. Ist niemand mehr drin, startet die nächste Session neu.")
            }

            if !room.requests.isEmpty && room.isAdmin {
                Section("Beitritts-Anfragen") {
                    ForEach(room.requests) { r in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(r.name)
                                Text(r.email).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button("Annehmen") { Task { await room.approve(r.userId, ok: true) } }
                            Button("Ablehnen") { Task { await room.approve(r.userId, ok: false) } }
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }

            Section("Clan-Sounds (\(room.sounds.count))") {
                if room.sounds.isEmpty {
                    Text("Noch keine Sounds im Clan. Lade welche aus deiner Bibliothek hoch.")
                        .foregroundStyle(.secondary)
                }
                ForEach(room.sounds) { s in
                    HStack {
                        Image(systemName: "waveform").foregroundStyle(.secondary)
                        Text(s.title)
                        Spacer()
                        Button {
                            Task { await room.takeToSolo(s) }
                        } label: {
                            Image(systemName: "square.and.arrow.down")
                        }
                        .buttonStyle(.borderless)
                        .help("In deine eigenen Sounds übernehmen")
                    }
                }
                Button {
                    Task { await room.upload(pickSounds()) }
                } label: {
                    Label("Sounds in den Clan laden…", systemImage: "square.and.arrow.up")
                }
                .disabled(room.busy)
            }

            Members(room: room)

            if room.isAdmin { AdminInvite(room: room) }

            if room.busy { Section { ProgressView() } }
            if let n = room.note { Section { Text(n).foregroundStyle(.secondary) } }
        }
        .formStyle(.grouped)
        .navigationTitle(room.name)
        .task { await room.load() }
    }

    /// Audio-Dateien auswählen (zum Hochladen in den Clan).
    private func pickSounds() -> [Sound] {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.message = "Sounds für den Clan auswählen"
        guard panel.runModal() == .OK else { return [] }
        return panel.urls.map { Sound(url: $0) }
    }
}

/// Mitglieder-Liste, standardmäßig eingeklappt (versteckt wie Admin-Kram).
private struct Members: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        Section {
            if show {
                ForEach(room.members) { m in
                    Label {
                        Text(m.name)
                    } icon: {
                        Image(systemName: m.role == "admin" ? "crown" : "person")
                    }
                }
            }
        } header: {
            Button(show ? "Mitglieder ausblenden" : "Mitglieder zeigen (\(room.members.count))") { show.toggle() }
                .buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// Invite-Code – versteckt, erscheint erst nach 3× Klick auf die Überschrift (wie die System-Lautstärke).
private struct AdminInvite: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        Section {
            if show {
                LabeledContent("Invite-Code", value: room.inviteCode ?? "–")
                    .textSelection(.enabled)
                Button("Neuen Code erzeugen") { Task { await room.rotateInvite() } }
            }
        } header: {
            Text("Admin")
                .onTapGesture(count: 3) { withAnimation { show.toggle() } } // versteckt: 3× tippen
        } footer: {
            if !show { Text("3× auf „Admin“ tippen für den Invite-Code.").font(.caption2) }
        }
    }
}
