import SwiftUI
import AppKit

/// Clan-Seite – wie ein Discord-Server: Session, Einladen, Clan-Sounds, Mitglieder (wer online ist).
/// Aktualisiert sich von selbst, wenn sich im Clan etwas ändert.
struct ClanPage: View {
    @StateObject private var room: ClanRoom
    @State private var pendingDelete: ClanSound?
    @State private var pendingKick: ClanMember?
    @State private var confirmLeave = false
    @Environment(\.dismiss) private var dismiss

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
                        HStack(spacing: 8) {
                            Badge(text: room.owner ? "Besitzer" : room.isAdmin ? "Admin" : "Mitglied", color: room.isAdmin ? .orange : .secondary)
                            Text("\(room.online) von \(room.members.count) online").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                }

                // Session
                VStack(alignment: .leading, spacing: 10) {
                    Button {
                        Task { await room.startSession() }
                    } label: {
                        Label(room.sessionCode == nil ? "Clan-Session starten" : "Session beitreten", systemImage: "play.circle.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                    .disabled(room.busy)
                    if room.lobby.active {
                        Button("Session verlassen", role: .destructive) { room.lobby.leave() }.controlSize(.small)
                    }
                    let inSession = room.members.filter(\.inSession).map(\.name)
                    Text(inSession.isEmpty
                         ? "Alle im Clan können beitreten und zusammen abspielen."
                         : "Gerade in der Session: \(inSession.joined(separator: ", "))")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .clanCard()

                InviteCard(room: room)

                // Clan-Sounds
                VStack(alignment: .leading, spacing: 10) {
                    HStack {
                        Text("Clan-Sounds").font(.headline)
                        Spacer()
                        Text("\(room.sounds.count)").foregroundStyle(.secondary).monospacedDigit()
                    }
                    if room.sounds.isEmpty {
                        Text("Noch keine Sounds im Clan. Teile welche aus deiner Bibliothek.").foregroundStyle(.secondary)
                    }
                    ForEach(room.sounds) { s in
                        HStack(spacing: 10) {
                            Image(systemName: "waveform").foregroundStyle(s.available ? Color.accentColor : .secondary)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(s.title).foregroundStyle(s.available ? .primary : .secondary)
                                Text(s.available ? (s.by.map { "von \($0)" } ?? "verfügbar")
                                                 : "gerade niemand online, der ihn hat\(s.by.map { " · von \($0)" } ?? "")")
                                    .font(.caption).foregroundStyle(.tertiary)
                            }
                            Spacer()
                            Button { Task { await room.takeToSolo(s) } } label: { Image(systemName: "square.and.arrow.down") }
                                .buttonStyle(.borderless).help("In deine eigenen Sounds übernehmen")
                                .disabled(!s.available || room.busy)
                            if room.isAdmin {
                                Button { pendingDelete = s } label: { Image(systemName: "trash") }
                                    .buttonStyle(.borderless).foregroundStyle(.red).help("Aus dem Clan löschen (nur Admins)")
                            }
                        }
                        .padding(.vertical, 2)
                    }
                    Button { Task { await room.share(pickSounds()) } } label: {
                        Label("Sounds im Clan teilen…", systemImage: "square.and.arrow.up")
                    }
                    .disabled(room.busy)
                    .padding(.top, 2)
                    Text("Die Sounds bleiben auf euren Geräten und gehen direkt von einem zum anderen. Der Server speichert keine Dateien.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                .clanCard()

                MembersCard(room: room, onKick: { pendingKick = $0 })

                Button(room.owner ? "Clan auflösen …" : "Clan verlassen …", role: .destructive) { confirmLeave = true }
                    .buttonStyle(.link).foregroundStyle(.red).font(.callout)

                if room.busy { ProgressView().controlSize(.small) }
                if let n = room.note { Text(n).font(.callout).foregroundStyle(.secondary) }
            }
            .padding(24)
        }
        .navigationTitle(room.name)
        .task { await room.load() }
        .onChange(of: room.gone) { _, gone in if gone { dismiss() } }
        .confirmationDialog("„\(pendingDelete?.title ?? "")“ aus dem Clan löschen?", isPresented: binding($pendingDelete), presenting: pendingDelete) { s in
            Button("Löschen", role: .destructive) { Task { await room.delete(s) } }
        } message: { _ in
            Text("Der Sound verschwindet für alle im Clan. Wer ihn schon in seine eigenen Sounds übernommen hat, behält ihn.")
        }
        .confirmationDialog("\(pendingKick?.name ?? "") aus dem Clan entfernen?", isPresented: binding($pendingKick), presenting: pendingKick) { m in
            Button("Entfernen", role: .destructive) { Task { await room.kick(m) } }
        } message: { _ in
            Text("Mit dem Invite-Code kommt die Person wieder rein. Erzeug danach einen neuen Code, wenn sie draußen bleiben soll.")
        }
        .confirmationDialog(room.owner ? "Clan „\(room.name)“ auflösen?" : "Clan „\(room.name)“ verlassen?", isPresented: $confirmLeave) {
            Button(room.owner ? "Auflösen" : "Verlassen", role: .destructive) { Task { await room.leaveOrDissolve() } }
        } message: {
            Text(room.owner ? "Der Clan ist dann für alle weg, mit allen Clan-Sounds. Eigene Sounds bleiben auf den Geräten."
                            : "Mit dem Invite-Code kannst du wieder beitreten.")
        }
    }

    private func binding<T>(_ value: Binding<T?>) -> Binding<Bool> {
        Binding(get: { value.wrappedValue != nil }, set: { if !$0 { value.wrappedValue = nil } })
    }

    private func pickSounds() -> [URL] {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.directoryURL = room.library.folder
        panel.message = "Sounds zum Teilen auswählen – sie bleiben auf deinem Mac"
        guard panel.runModal() == .OK else { return [] }
        return panel.urls
    }
}

/// Einladen – wie bei Discord: wer den Code oder Link hat, ist sofort im Clan.
private struct InviteCard: View {
    @ObservedObject var room: ClanRoom
    @State private var copied = false
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label("Freunde einladen", systemImage: "person.badge.plus").font(.headline)
            HStack(spacing: 12) {
                Text(room.inviteCode ?? "–").font(.title2.monospaced().weight(.semibold)).textSelection(.enabled)
                Spacer()
                Button(copied ? "Kopiert ✓" : "Link kopieren") {
                    guard let link = room.inviteLink else { return }
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(link, forType: .string)
                    copied = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2) { copied = false }
                }
                .disabled(room.inviteLink == nil)
            }
            Text("Wer den Code oder Link hat, ist mit „Clan beitreten“ sofort drin.")
                .font(.caption).foregroundStyle(.secondary)
            if room.isAdmin {
                Button("Neuen Code erzeugen (alter gilt dann nicht mehr)") { Task { await room.rotateInvite() } }
                    .buttonStyle(.link).font(.caption)
            }
        }
        .clanCard()
    }
}

/// Mitglieder – wer online ist, wer in der Session ist; Admins können Leute entfernen.
private struct MembersCard: View {
    @ObservedObject var room: ClanRoom
    let onKick: (ClanMember) -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Mitglieder (\(room.members.count))").font(.headline)
            ForEach(room.members) { m in
                HStack(spacing: 10) {
                    ZStack(alignment: .bottomTrailing) {
                        Avatar(name: m.name, size: 28)
                        Circle().fill(m.online ? Color.green : Color.gray.opacity(0.6))
                            .frame(width: 10, height: 10)
                            .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 2))
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        Text(m.name).foregroundStyle(m.online ? .primary : .secondary)
                        Text(m.inSession ? "in der Session" : m.online ? "online" : "offline").font(.caption).foregroundStyle(.tertiary)
                    }
                    Spacer()
                    if m.owner { Badge(text: "Besitzer", color: .orange) }
                    else if m.isAdmin { Badge(text: "Admin", color: .orange) }
                    if room.isAdmin && !m.owner && m.id != room.account.user?.id {
                        Button { onKick(m) } label: { Image(systemName: "person.fill.xmark") }
                            .buttonStyle(.borderless).foregroundStyle(.red).help("Aus dem Clan entfernen")
                    }
                }
            }
        }
        .clanCard()
    }
}
