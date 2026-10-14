import SwiftUI
import AppKit
import CryptoKit

struct ClanMember: Identifiable, Equatable { let id: String; let name: String; let role: String }
struct ClanRequest: Identifiable, Equatable { var id: String { userId }; let userId: String; let name: String; let email: String }
struct ClanSound: Identifiable, Equatable {
    let id: String; let name: String; let size: Int
    var title: String {
        (name as NSString).deletingPathExtension.replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
    }
}

/// Eine geöffnete Clan-Seite: Mitglieder, Anfragen (Admin), Clan-Sounds, Session.
@MainActor
final class ClanRoom: ObservableObject {
    let account: Account
    let lobby: Lobby
    let library: SoundLibrary
    let clanId: String

    @Published var name: String
    @Published var role = ""
    @Published var owner = false
    @Published var inviteCode: String?
    @Published var sessionCode: String?
    @Published var members: [ClanMember] = []
    @Published var requests: [ClanRequest] = []
    @Published var sounds: [ClanSound] = []
    @Published var busy = false
    @Published var note: String?

    var isAdmin: Bool { role == "admin" }

    init(account: Account, lobby: Lobby, library: SoundLibrary, clanId: String, name: String) {
        self.account = account; self.lobby = lobby; self.library = library
        self.clanId = clanId; self.name = name
    }

    func load() async {
        busy = true; defer { busy = false }
        if case .success(let o) = await account.api("api/clans/\(clanId)") {
            if let c = o["clan"] as? [String: Any] {
                name = c["name"] as? String ?? name
                role = c["role"] as? String ?? ""
                owner = c["owner"] as? Bool ?? false
                inviteCode = c["inviteCode"] as? String
                sessionCode = c["session"] as? String
            }
            members = (o["members"] as? [[String: Any]])?.compactMap {
                guard let id = $0["id"] as? String, let name = $0["name"] as? String, let role = $0["role"] as? String else { return nil }
                return ClanMember(id: id, name: name, role: role)
            } ?? []
            requests = (o["requests"] as? [[String: Any]])?.compactMap {
                guard let uid = $0["userId"] as? String, let name = $0["name"] as? String else { return nil }
                return ClanRequest(userId: uid, name: name, email: $0["email"] as? String ?? "")
            } ?? []
        }
        if case .success(let o) = await account.api("api/clans/\(clanId)/sounds") {
            sounds = (o["sounds"] as? [[String: Any]])?.compactMap {
                guard let id = $0["id"] as? String, let name = $0["name"] as? String else { return nil }
                return ClanSound(id: id, name: name, size: ($0["size"] as? Int) ?? 0)
            } ?? []
        }
    }

    func approve(_ userId: String, ok: Bool) async {
        _ = await account.api("api/clans/\(clanId)/approve", method: "POST", body: ["userId": userId, "ok": ok])
        await load()
    }

    func rotateInvite() async {
        if case .success(let o) = await account.api("api/clans/\(clanId)/invite", method: "POST") {
            inviteCode = o["inviteCode"] as? String
            note = "Neuer Invite-Code: \(inviteCode ?? "–")"
        }
    }

    /// Lokale Sounds in die Clan-Bibliothek laden.
    func upload(_ files: [Sound]) async {
        guard !files.isEmpty else { return }
        busy = true; defer { busy = false }
        var meta: [[String: Any]] = [], byId: [String: Data] = [:]
        for s in files {
            guard let data = try? Data(contentsOf: s.url) else { continue }
            let sha = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            meta.append(["id": sha, "name": s.url.lastPathComponent, "size": data.count])
            byId[sha] = data
        }
        guard case .success(let o) = await account.api("api/clans/\(clanId)/sounds", method: "POST", body: ["sounds": meta]) else {
            note = account.problem ?? "Hochladen fehlgeschlagen."; return
        }
        for id in (o["missing"] as? [String]) ?? [] {
            guard let data = byId[id] else { continue }
            await putBlob(id: id, data: data)
        }
        await load()
        note = "In den Clan geladen."
    }

    private func putBlob(id: String, data: Data) async {
        var req = URLRequest(url: Account.server.appendingPathComponent("api/clans/\(clanId)/blobs/\(id)"))
        req.httpMethod = "PUT"
        if let t = account.token { req.setValue("Bearer \(t)", forHTTPHeaderField: "Authorization") }
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        req.httpBody = data
        _ = try? await URLSession.shared.data(for: req)
    }

    /// Clan-Sound in die eigenen (Solo-)Sounds übernehmen.
    func takeToSolo(_ s: ClanSound) async {
        var req = URLRequest(url: Account.server.appendingPathComponent("api/clans/\(clanId)/blobs/\(s.id)"))
        if let t = account.token { req.setValue("Bearer \(t)", forHTTPHeaderField: "Authorization") }
        guard let (data, resp) = try? await URLSession.shared.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200 else { note = "Download fehlgeschlagen."; return }
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(s.name)
        try? data.write(to: tmp)
        let r = library.importItems([tmp])
        try? FileManager.default.removeItem(at: tmp)
        note = r.added > 0 ? "„\(s.title)“ in deine Sounds übernommen." : "Konnte nicht übernommen werden."
    }

    /// Clan-Session starten oder beitreten → läuft über den normalen Lobby-Kanal.
    func startSession() async {
        busy = true; defer { busy = false }
        guard case .success(let o) = await account.api("api/clans/\(clanId)/session", method: "POST"),
              let code = o["code"] as? String else { note = account.problem ?? "Session fehlgeschlagen."; return }
        if lobby.active { lobby.leave() }
        if let n = account.user?.name { lobby.name = n }
        lobby.joinSession(code: code, hostToken: o["hostToken"] as? String)
        sessionCode = code
        note = "Session läuft (Code \(code)). Spielen und beitreten wie in einer Lobby."
    }
}

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
