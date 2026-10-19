import SwiftUI
import AppKit

/// Clan-Seite – wie ein Discord-Server: Session, Einladen, Clan-Sounds, Mitglieder (wer online ist).
/// Aktualisiert sich von selbst, wenn sich im Clan etwas ändert.
struct ClanPage: View {
    @StateObject private var room: ClanRoom
    @State private var pendingDelete: ClanSound?
    @State private var pendingKick: ClanMember?
    @State private var confirmLeave = false
    @State private var copied = false
    /// Nicht mehr im Clan: Seite verlassen
    let onClose: () -> Void

    init(account: Account, lobby: Lobby, library: SoundLibrary, clan: ClanSummary, onClose: @escaping () -> Void) {
        _room = StateObject(wrappedValue: ClanRoom(account: account, lobby: lobby, library: library, clanId: clan.id, name: clan.name))
        self.onClose = onClose
    }

    var body: some View {
        Form {
            // Kopf + Session
            Section {
                HStack(spacing: 16) {
                    ClanIcon(name: room.name, key: room.clanId, size: 60)
                    VStack(alignment: .leading, spacing: 5) {
                        Text(room.name).font(.title2.weight(.bold))
                        HStack(spacing: 8) {
                            Badge(text: room.owner ? "Besitzer" : room.isAdmin ? "Admin" : "Mitglied", color: room.isAdmin ? .orange : .secondary)
                            Circle().fill(room.online > 0 ? Color.green : Color.gray).frame(width: 7, height: 7)
                            Text("\(room.online) von \(room.members.count) online").font(.callout).foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                    VStack(alignment: .trailing, spacing: 6) {
                        Button {
                            Task { await room.startSession() }
                        } label: {
                            Label(room.sessionCode == nil ? "Session starten" : "Session beitreten", systemImage: "play.fill")
                        }
                        .glassButton(prominent: true)
                        .controlSize(.large)
                        .disabled(room.busy)
                        if room.lobby.active {
                            Button("Session verlassen", role: .destructive) { room.lobby.leave() }
                                .buttonStyle(.link)
                                .foregroundStyle(.red)
                                .font(.callout)
                        }
                    }
                }
                .padding(.vertical, 6)
            } footer: {
                let inSession = room.members.filter(\.inSession).map(\.name)
                Text(inSession.isEmpty
                     ? "Alle im Clan können beitreten und zusammen abspielen."
                     : "Gerade in der Session: \(inSession.joined(separator: ", "))")
            }

            // Einladen – wie bei Discord: wer den Code oder Link hat, ist sofort im Clan
            Section {
                LabeledContent("Invite-Code") {
                    HStack(spacing: 10) {
                        Text(room.inviteCode ?? "–")
                            .font(.title3.monospaced().weight(.semibold))
                            .textSelection(.enabled)
                        Button(copied ? "Kopiert" : "Link kopieren", systemImage: copied ? "checkmark" : "link") {
                            guard let link = room.inviteLink else { return }
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(link, forType: .string)
                            copied = true
                            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { copied = false }
                        }
                        .disabled(room.inviteLink == nil)
                    }
                }
                if room.isAdmin {
                    Button("Neuen Code erzeugen …") { Task { await room.rotateInvite() } }
                        .help("Der alte Code gilt dann nicht mehr")
                }
            } header: {
                Text("Freunde einladen")
            } footer: {
                Text("Wer den Code oder Link hat, ist mit „Clan beitreten“ sofort drin.")
            }

            // Clan-Sounds
            Section {
                if room.sounds.isEmpty {
                    Text("Noch keine Sounds im Clan. Teile welche aus deiner Bibliothek.").foregroundStyle(.secondary)
                }
                ForEach(room.sounds) { s in
                    HStack(spacing: 12) {
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(s.available ? AnyShapeStyle(Palette.gradient(Palette.color(for: s.name))) : AnyShapeStyle(.quaternary))
                            .frame(width: 30, height: 30)
                            .overlay(Image(systemName: "waveform").font(.system(size: 13, weight: .bold)).foregroundStyle(.white))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(s.title).foregroundStyle(s.available ? .primary : .secondary)
                            Text(s.available ? (s.by.map { "von \($0)" } ?? "verfügbar")
                                             : "gerade niemand online, der ihn hat\(s.by.map { " · von \($0)" } ?? "")")
                                .font(.caption).foregroundStyle(.secondary)
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
                }
                Button { Task { await room.share(pickSounds()) } } label: {
                    Label("Sounds im Clan teilen …", systemImage: "square.and.arrow.up")
                }
                .disabled(room.busy)
            } header: {
                HStack {
                    Text("Clan-Sounds")
                    Spacer()
                    Text("\(room.sounds.count)").monospacedDigit()
                }
            } footer: {
                Text("Die Sounds bleiben auf euren Geräten und gehen direkt von einem zum anderen. Der Server speichert keine Dateien.")
            }

            // Mitglieder – wer online ist, wer in der Session ist; Admins können Leute entfernen
            Section("Mitglieder (\(room.members.count))") {
                ForEach(room.members) { m in
                    HStack(spacing: 10) {
                        ZStack(alignment: .bottomTrailing) {
                            Avatar(name: m.name, size: 30)
                            Circle().fill(m.online ? Color.green : Color.gray.opacity(0.6))
                                .frame(width: 10, height: 10)
                                .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 2))
                        }
                        VStack(alignment: .leading, spacing: 1) {
                            Text(m.name).foregroundStyle(m.online ? .primary : .secondary)
                            Text(m.inSession ? "in der Session" : m.online ? "online" : "offline").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if m.owner { Badge(text: "Besitzer", color: .orange) }
                        else if m.isAdmin { Badge(text: "Admin", color: .orange) }
                        if room.isAdmin && !m.owner && m.id != room.account.user?.id {
                            Button { pendingKick = m } label: { Image(systemName: "person.fill.xmark") }
                                .buttonStyle(.borderless).foregroundStyle(.red).help("Aus dem Clan entfernen")
                        }
                    }
                }
            }

            Section {
                Button(room.owner ? "Clan auflösen …" : "Clan verlassen …", role: .destructive) { confirmLeave = true }
                    .foregroundStyle(.red)
            } footer: {
                VStack(alignment: .leading, spacing: 4) {
                    if room.busy { ProgressView().controlSize(.small) }
                    if let n = room.note { Text(n) }
                }
            }
        }
        .formStyle(.grouped)
        .softScrollEdges()
        .navigationTitle(room.name)
        .navigationSubtitle("Clan")
        .task { await room.load() }
        .onChange(of: room.gone) { _, gone in if gone { onClose() } }
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
