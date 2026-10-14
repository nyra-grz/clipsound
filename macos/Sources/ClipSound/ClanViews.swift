import SwiftUI
import AppKit

/// Clan-Seite – übersichtlich in Karten statt Formular.
struct ClanPage: View {
    @StateObject private var room: ClanRoom

    init(account: Account, lobby: Lobby, library: SoundLibrary, clan: ClanSummary) {
        _room = StateObject(wrappedValue: ClanRoom(account: account, lobby: lobby, library: library, clanId: clan.id, name: clan.name))
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                // Kopf
                HStack(spacing: 14) {
                    Image(systemName: room.isAdmin ? "crown.fill" : "person.3.fill")
                        .font(.system(size: 34))
                        .foregroundStyle(room.isAdmin ? Color.orange : Color.secondary)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(room.name).font(.title2.weight(.bold))
                        Badge(text: room.isAdmin ? "Admin" : "Mitglied", color: room.isAdmin ? .orange : .secondary)
                    }
                    Spacer()
                }

                // Session
                VStack(alignment: .leading, spacing: 10) {
                    Button {
                        Task { await room.startSession() }
                    } label: {
                        Label(room.sessionCode == nil ? "Clan-Session starten" : "Session beitreten (\(room.sessionCode!))",
                              systemImage: "play.circle.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                    .disabled(room.busy)
                    if room.lobby.active {
                        Button("Session verlassen", role: .destructive) { room.lobby.leave() }.controlSize(.small)
                    }
                    Text("Alle im Clan können beitreten und zusammen abspielen. Ist niemand mehr drin, startet die nächste Session neu.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .clanCard()

                // Anfragen (Admin)
                if room.isAdmin && !room.requests.isEmpty {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Beitritts-Anfragen").font(.headline)
                        ForEach(room.requests) { r in
                            HStack(spacing: 10) {
                                Avatar(name: r.name, size: 32)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(r.name).font(.subheadline.weight(.medium))
                                    Text(r.email).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Button("Annehmen") { Task { await room.approve(r.userId, ok: true) } }
                                    .buttonStyle(.borderedProminent).controlSize(.small)
                                Button("Ablehnen") { Task { await room.approve(r.userId, ok: false) } }.controlSize(.small)
                            }
                        }
                    }
                    .clanCard()
                }

                // Clan-Sounds
                VStack(alignment: .leading, spacing: 10) {
                    HStack {
                        Text("Clan-Sounds").font(.headline)
                        Spacer()
                        Text("\(room.sounds.count)").foregroundStyle(.secondary).monospacedDigit()
                    }
                    if room.sounds.isEmpty {
                        Text("Noch keine Sounds im Clan. Lade welche aus deiner Bibliothek hoch.").foregroundStyle(.secondary)
                    }
                    ForEach(room.sounds) { s in
                        HStack(spacing: 10) {
                            Image(systemName: "waveform").foregroundStyle(.secondary)
                            Text(s.title)
                            Spacer()
                            Button { Task { await room.takeToSolo(s) } } label: { Image(systemName: "square.and.arrow.down") }
                                .buttonStyle(.borderless).help("In deine eigenen Sounds übernehmen")
                        }
                        .padding(.vertical, 2)
                    }
                    Button { Task { await room.upload(pickSounds()) } } label: {
                        Label("Sounds in den Clan laden…", systemImage: "square.and.arrow.up")
                    }
                    .disabled(room.busy)
                    .padding(.top, 2)
                }
                .clanCard()

                MembersCard(room: room)
                if room.isAdmin { AdminInviteCard(room: room) }

                if room.busy { ProgressView().controlSize(.small) }
                if let n = room.note { Text(n).font(.callout).foregroundStyle(.secondary) }
            }
            .padding(24)
        }
        .navigationTitle(room.name)
        .task { await room.load() }
    }

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

/// Mitglieder – aufklappbar.
private struct MembersCard: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Button { withAnimation { show.toggle() } } label: {
                HStack {
                    Text("Mitglieder (\(room.members.count))").font(.headline)
                    Spacer()
                    Image(systemName: show ? "chevron.up" : "chevron.down").foregroundStyle(.secondary)
                }
            }
            .buttonStyle(.plain)
            if show {
                ForEach(room.members) { m in
                    HStack(spacing: 10) {
                        Avatar(name: m.name, size: 26)
                        Text(m.name)
                        Spacer()
                        if m.role == "admin" { Badge(text: "Admin", color: .orange) }
                    }
                }
            }
        }
        .clanCard()
    }
}

/// Invite-Code versteckt – 3× auf „Admin“ tippen (wie die System-Lautstärke).
private struct AdminInviteCard: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Admin").font(.headline)
                .onTapGesture(count: 3) { withAnimation { show.toggle() } }
            if show {
                HStack {
                    Text("Invite-Code").foregroundStyle(.secondary)
                    Spacer()
                    Text(room.inviteCode ?? "–").font(.title3.monospaced()).textSelection(.enabled)
                }
                Button("Neuen Code erzeugen") { Task { await room.rotateInvite() } }.controlSize(.small)
            } else {
                Text("3× auf „Admin“ tippen für den Invite-Code.").font(.caption).foregroundStyle(.tertiary)
            }
        }
        .clanCard()
    }
}
