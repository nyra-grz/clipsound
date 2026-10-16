import SwiftUI
import Combine
import Foundation
#if canImport(AppKit)
import AppKit
#endif

// Konto + Clans gegen den ClipSound-Server (cloud/clans.js). Solo-Modus läuft weiter ohne Konto
// über Lobby.swift; hier geht es nur um angemeldete Nutzer und ihre Clans.
// Clan-Sounds liegen nicht auf dem Server, sondern auf den Geräten der Mitglieder (siehe Share.swift).

struct AccountUser: Equatable {
    let id: String
    let email: String
    let name: String
}

/// Server-Fehler mit lesbarer Meldung.
struct Fail: Error { let message: String }

struct ClanSummary: Identifiable, Equatable, Hashable {
    let id: String
    let name: String
    let role: String        // "admin" | "member"
    let members: Int
    var isAdmin: Bool { role == "admin" }
}

@MainActor
final class Account: ObservableObject {
    /// Es gibt nur ein Konto pro App – sonst hätte jedes neu geöffnete Fenster eine eigene Geräte-Verbindung,
    /// und die alten blieben als tote Verbindungen beim Server hängen.
    static let shared = Account()

    static let server: URL = ProcessInfo.processInfo.environment["CLIPSOUND_SERVER"].flatMap(URL.init(string:))
        ?? URL(string: "https://rooms-production-8e28.up.railway.app")!
    /// Datenschutzerklärung (gilt für alle Versionen)
    static var privacyURL: URL { server.appendingPathComponent("datenschutz") }

    @Published private(set) var user: AccountUser?
    @Published private(set) var clans: [ClanSummary] = []
    @Published var busy = false
    @Published var problem: String?

    private(set) var token: String? {
        didSet {
            if let token { UserDefaults.standard.set(token, forKey: "accountToken"); share.start(token: token) }
            else { UserDefaults.standard.removeObject(forKey: "accountToken"); share.stop() }
        }
    }

    var loggedIn: Bool { user != nil }

    /// Gibt Clan-Sounds aus der eigenen Bibliothek an andere Mitglieder weiter, solange man angemeldet ist.
    let share = ClanShare(server: Account.server)
    /// Live-Updates vom Server: in diesem Clan hat sich etwas geändert
    let clanEvents = PassthroughSubject<String, Never>()
    private var librarySub: AnyCancellable?
    private var refreshTask: Task<Void, Never>?

    init() {
        token = UserDefaults.standard.string(forKey: "accountToken")
        share.onClanChanged = { [weak self] clanId in
            Task { @MainActor in
                self?.clanEvents.send(clanId)
                self?.refreshSoon() // Clan-Liste (beigetreten, entfernt, aufgelöst)
            }
        }
        if let token { share.start(token: token); Task { await refreshMe() } }
        #if canImport(AppKit)
        // Nach dem Ruhezustand ist die alte Verbindung oft tot
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.appBecameActive() }
        }
        #endif
    }

    /// App ist wieder vorne (iPhone) oder aufgewacht (Mac): frisch verbinden und neu laden.
    func appBecameActive() {
        guard token != nil else { return }
        share.reconnectNow()
        refreshSoon()
    }

    private func refreshSoon() {
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 300_000_000)
            guard !Task.isCancelled else { return }
            await self?.refreshMe()
        }
    }

    /// Die eigene Bibliothek für Clans bereitstellen (die App ruft das einmal auf).
    func attach(_ library: SoundLibrary) {
        guard librarySub == nil else { return }
        librarySub = library.$sounds.sink { [weak self] sounds in self?.share.filesChanged(sounds.map(\.url)) }
    }

    // MARK: Netzwerk

    /// Ergebnis mit dem JSON-Objekt oder einer Fehlermeldung.
    func api(_ path: String, method: String = "GET", body: [String: Any]? = nil) async -> Result<[String: Any], Fail> {
        var req = URLRequest(url: Self.server.appendingPathComponent(path))
        req.httpMethod = method
        if let token { req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            if (200..<300).contains(code) { return .success(obj) }
            return .failure(Fail(message: obj["error"] as? String ?? "Fehler \(code)"))
        } catch {
            return .failure(Fail(message: "Keine Verbindung zum Server."))
        }
    }

    private func userFrom(_ d: Any?) -> AccountUser? {
        guard let d = d as? [String: Any], let id = d["id"] as? String,
              let email = d["email"] as? String, let name = d["name"] as? String else { return nil }
        return AccountUser(id: id, email: email, name: name)
    }

    private func clansFrom(_ a: Any?) -> [ClanSummary] {
        (a as? [[String: Any]])?.compactMap { d in
            guard let id = d["id"] as? String, let name = d["name"] as? String, let role = d["role"] as? String else { return nil }
            return ClanSummary(id: id, name: name, role: role, members: (d["members"] as? Int) ?? 0)
        } ?? []
    }

    // MARK: Anmeldung

    /// Registrieren – schickt den Bestätigungscode per Mail. true = Code ist unterwegs.
    func register(email: String, name: String, password: String) async -> Bool {
        busy = true; problem = nil; defer { busy = false }
        switch await api("api/account/register", method: "POST", body: ["email": email, "name": name, "password": password]) {
        case .success: return true
        case .failure(let e): problem = e.message; return false
        }
    }

    func resend(email: String) async {
        _ = await api("api/account/resend", method: "POST", body: ["email": email])
    }

    /// Code bestätigen → eingeloggt.
    func verify(email: String, code: String) async -> Bool {
        busy = true; problem = nil; defer { busy = false }
        switch await api("api/account/verify", method: "POST", body: ["email": email, "code": code]) {
        case .success(let o):
            guard let t = o["token"] as? String, let u = userFrom(o["user"]) else { problem = "Unerwartete Antwort"; return false }
            token = t; user = u
            await refreshMe()
            return true
        case .failure(let e): problem = e.message; return false
        }
    }

    /// true = eingeloggt. needsVerify = Konto existiert, aber E-Mail noch nicht bestätigt.
    func login(email: String, password: String) async -> (ok: Bool, needsVerify: Bool) {
        busy = true; problem = nil; defer { busy = false }
        switch await api("api/account/login", method: "POST", body: ["email": email, "password": password]) {
        case .success(let o):
            guard let t = o["token"] as? String, let u = userFrom(o["user"]) else { problem = "Unerwartete Antwort"; return (false, false) }
            token = t; user = u
            await refreshMe()
            return (true, false)
        case .failure(let e):
            problem = e.message
            return (false, e.message.lowercased().contains("bestätig"))
        }
    }

    func logout() {
        // Den Schlüssel auch auf dem Server ungültig machen
        if let old = token {
            var req = URLRequest(url: Self.server.appendingPathComponent("api/account/logout"))
            req.httpMethod = "POST"
            req.setValue("Bearer \(old)", forHTTPHeaderField: "Authorization")
            Task { _ = try? await URLSession.shared.data(for: req) }
        }
        token = nil; user = nil; clans = []
    }

    /// Konto mit allen Daten löschen (DSGVO Art. 17). Braucht das Passwort.
    func deleteAccount(password: String) async -> Bool {
        busy = true; problem = nil; defer { busy = false }
        switch await api("api/account/delete", method: "POST", body: ["password": password]) {
        case .success:
            token = nil; user = nil; clans = []
            return true
        case .failure(let e):
            problem = e.message
            return false
        }
    }

    /// „Wer bin ich“ + eigene Clans laden. Bei 401 wird ausgeloggt.
    func refreshMe() async {
        guard token != nil else { return }
        switch await api("api/account/me") {
        case .success(let o):
            if let u = userFrom(o["user"]) { user = u }
            clans = clansFrom(o["clans"])
        case .failure(let e):
            if e.message.contains("Nicht eingeloggt") || e.message.contains("401") { logout() }
        }
    }

    // MARK: Clans

    /// Neuen Clan anlegen (du wirst Admin). Gibt den Invite-Code zurück.
    func createClan(name: String) async -> String? {
        busy = true; problem = nil; defer { busy = false }
        switch await api("api/clans", method: "POST", body: ["name": name]) {
        case .success(let o):
            await refreshMe()
            return (o["clan"] as? [String: Any])?["inviteCode"] as? String
        case .failure(let e): problem = e.message; return nil
        }
    }

    /// Mit Invite-Code oder -Link beitreten – wie bei Discord ist man direkt drin. Ergebnis: "member".
    func joinClan(code: String) async -> String? {
        busy = true; problem = nil; defer { busy = false }
        switch await api("api/clans/join", method: "POST", body: ["code": code]) {
        case .success(let o):
            await refreshMe()
            return o["status"] as? String
        case .failure(let e): problem = e.message; return nil
        }
    }
}

// MARK: - Clan-Modell (geteilt mit iOS)

struct ClanMember: Identifiable, Equatable {
    let id: String; let name: String; let role: String
    var owner = false
    /// ClipSound ist bei ihm gerade offen
    var online = false
    var inSession = false
    var isAdmin: Bool { role == "admin" }
}
struct ClanSound: Identifiable, Equatable {
    let id: String; let name: String; let size: Int
    /// Gerade ist jemand online, der ihn auf dem Gerät hat
    var available = true
    /// Wer ihn geteilt hat
    var by: String?
    var title: String {
        (name as NSString).deletingPathExtension.replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
    }
}

/// Eine geöffnete Clan-Seite (wie ein Discord-Server): Session, Einladung, Clan-Sounds, Mitglieder.
/// Lädt sich von selbst neu, wenn der Server meldet, dass sich etwas geändert hat.
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
    @Published var sounds: [ClanSound] = []
    @Published var busy = false
    @Published var note: String?
    /// Nicht mehr im Clan (entfernt, ausgetreten, aufgelöst) – die Seite schließt sich
    @Published var gone = false

    var isAdmin: Bool { role == "admin" }
    var online: Int { members.filter(\.online).count }
    /// Zum Weitergeben, öffnet eine Einladungsseite
    var inviteLink: String? { inviteCode.map { Account.server.appendingPathComponent("c/\($0)").absoluteString } }

    private var events: AnyCancellable?

    init(account: Account, lobby: Lobby, library: SoundLibrary, clanId: String, name: String) {
        self.account = account; self.lobby = lobby; self.library = library
        self.clanId = clanId; self.name = name
        events = account.clanEvents
            .filter { $0 == clanId }
            .debounce(for: .milliseconds(250), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in Task { await self?.load(quiet: true) } }
    }

    func load(quiet: Bool = false) async {
        if !quiet { busy = true }
        defer { if !quiet { busy = false } }
        switch await account.api("api/clans/\(clanId)") {
        case .success(let o):
            if let c = o["clan"] as? [String: Any] {
                name = c["name"] as? String ?? name
                role = c["role"] as? String ?? ""
                owner = c["owner"] as? Bool ?? false
                inviteCode = c["inviteCode"] as? String
                sessionCode = c["session"] as? String
            }
            members = (o["members"] as? [[String: Any]])?.compactMap {
                guard let id = $0["id"] as? String, let name = $0["name"] as? String, let role = $0["role"] as? String else { return nil }
                return ClanMember(id: id, name: name, role: role, owner: $0["owner"] as? Bool ?? false,
                                  online: $0["online"] as? Bool ?? false, inSession: $0["inSession"] as? Bool ?? false)
            }.sorted { a, b in a.online != b.online ? a.online : a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending } ?? []
        case .failure(let e):
            if e.message.contains("nicht in diesem Clan") || e.message.contains("gibt es nicht") { gone = true }
            return
        }
        if case .success(let o) = await account.api("api/clans/\(clanId)/sounds") {
            sounds = (o["sounds"] as? [[String: Any]])?.compactMap {
                guard let id = $0["id"] as? String, let name = $0["name"] as? String else { return nil }
                return ClanSound(id: id, name: name, size: ($0["size"] as? Int) ?? 0,
                                 available: ($0["available"] as? Bool) ?? true, by: $0["by"] as? String)
            } ?? []
        }
    }

    func rotateInvite() async {
        switch await account.api("api/clans/\(clanId)/invite", method: "POST") {
        case .success(let o):
            inviteCode = o["inviteCode"] as? String
            note = "Neuer Code: \(inviteCode ?? "–"). Der alte gilt nicht mehr."
        case .failure(let e): note = e.message
        }
    }

    /// Jemanden aus dem Clan entfernen (nur Admins, den Besitzer nicht).
    func kick(_ m: ClanMember) async {
        switch await account.api("api/clans/\(clanId)/kick", method: "POST", body: ["userId": m.id]) {
        case .success: note = "\(m.name) ist nicht mehr im Clan."
        case .failure(let e): note = e.message
        }
        await load(quiet: true)
    }

    /// Clan verlassen (alle außer dem Besitzer) bzw. auflösen (nur der Besitzer).
    func leaveOrDissolve() async {
        switch await account.api("api/clans/\(clanId)/\(owner ? "delete" : "leave")", method: "POST") {
        case .success:
            if lobby.active && lobby.code == sessionCode { lobby.leave() }
            gone = true
            await account.refreshMe()
        case .failure(let e): note = e.message
        }
    }

    /// Eigene Sounds im Clan teilen. Hochgeladen wird nichts: Die Dateien bleiben auf diesem Gerät und gehen
    /// direkt an die anderen, wenn jemand sie braucht. Dateien von außerhalb kommen dafür in die eigene Bibliothek.
    func share(_ files: [URL]) async {
        guard !files.isEmpty else { return }
        busy = true; defer { busy = false }
        let libraryFiles = library.sounds.map(\.url)
        let (meta, outside) = await Task.detached(priority: .userInitiated) { () -> ([[String: Any]], [URL]) in
            let inLibrary = Set(libraryFiles.map(\.standardizedFileURL.path))
            var meta: [[String: Any]] = [], outside: [(URL, String)] = []
            for url in files {
                guard let sha = FileHash.sha256(of: url), let size = FileHash.size(of: url) else { continue }
                meta.append(["id": sha, "name": url.lastPathComponent, "size": size])
                if !inLibrary.contains(url.standardizedFileURL.path) { outside.append((url, sha)) }
            }
            // Was es (mit gleichem Inhalt) schon in der Bibliothek gibt, nicht doppelt anlegen
            let known = outside.isEmpty ? [] : Set(libraryFiles.compactMap { FileHash.sha256(of: $0) })
            return (meta, outside.filter { !known.contains($0.1) }.map(\.0))
        }.value
        if !outside.isEmpty { _ = library.importItems(outside) }
        guard !meta.isEmpty else { note = "Keine Sounds gefunden."; return }
        switch await account.api("api/clans/\(clanId)/sounds", method: "POST", body: ["sounds": meta]) {
        case .success(let o):
            let skipped = (o["skipped"] as? [String])?.count ?? 0
            note = skipped > 0 ? "\(skipped) Sound(s) zu groß für den Clan (max. 15 MB pro Sound, 150 MB gesamt)."
                : "Geteilt. Die anderen bekommen die Sounds direkt von deinem Gerät, solange ClipSound bei dir offen ist."
        case .failure(let e):
            note = e.message
        }
        account.share.refresh() // gleich melden, dass sie hier liegen – sonst stehen sie kurz als „nicht verfügbar“ da
        await load()
    }

    /// Clan-Sound löschen (nur Admins).
    func delete(_ s: ClanSound) async {
        switch await account.api("api/clans/\(clanId)/sounds/\(s.id)", method: "DELETE") {
        case .success: note = "„\(s.title)“ aus dem Clan gelöscht."
        case .failure(let e): note = e.message
        }
        await load()
    }

    /// Clan-Sound in die eigenen (Solo-)Sounds übernehmen – kommt direkt vom Gerät eines Mitglieds.
    func takeToSolo(_ s: ClanSound) async {
        let mine = library.sounds.map(\.url)
        let have = await Task.detached(priority: .userInitiated) { mine.contains { FileHash.size(of: $0) == s.size && FileHash.sha256(of: $0) == s.id } }.value
        if have { note = "„\(s.title)“ ist schon in deinen Sounds."; return }
        var req = URLRequest(url: Account.server.appendingPathComponent("api/clans/\(clanId)/blobs/\(s.id)"))
        if let t = account.token { req.setValue("Bearer \(t)", forHTTPHeaderField: "Authorization") }
        busy = true; defer { busy = false }
        guard let (data, resp) = try? await URLSession.shared.data(for: req) else { note = "Keine Verbindung zum Server."; return }
        guard (resp as? HTTPURLResponse)?.statusCode == 200, FileHash.hex(data) == s.id else {
            note = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["error"] as? String ?? "Download fehlgeschlagen."
            return
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let tmp = dir.appendingPathComponent(s.name)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try? data.write(to: tmp)
        let r = library.importItems([tmp])
        try? FileManager.default.removeItem(at: dir)
        note = r.added > 0 ? "„\(s.title)“ in deine Sounds übernommen." : "Konnte nicht übernommen werden."
    }

    /// Clan-Session starten oder beitreten → läuft über den normalen Lobby-Kanal.
    func startSession() async {
        busy = true; defer { busy = false }
        let o: [String: Any]
        switch await account.api("api/clans/\(clanId)/session", method: "POST") {
        case .success(let r): o = r
        case .failure(let e): note = e.message; return
        }
        guard let code = o["code"] as? String, let ticket = o["ticket"] as? String else { note = "Session fehlgeschlagen."; return }
        if lobby.active { lobby.leave() }
        lobby.joinSession(code: code, ticket: ticket)
        sessionCode = code
        note = "Session läuft (Code \(code)). Spielen und beitreten wie in einer Lobby."
    }
}
