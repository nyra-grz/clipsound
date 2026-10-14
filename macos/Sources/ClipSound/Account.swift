import SwiftUI
import Foundation

// Konto + Clans gegen den ClipSound-Server (cloud/clans.js). Solo-Modus läuft weiter ohne Konto
// über Lobby.swift; hier geht es nur um angemeldete Nutzer und ihre Clans.

struct AccountUser: Equatable {
    let id: String
    let email: String
    let name: String
}

/// Server-Fehler mit lesbarer Meldung.
struct Fail: Error { let message: String }

struct ClanSummary: Identifiable, Equatable {
    let id: String
    let name: String
    let role: String        // "admin" | "member"
    let members: Int
    var isAdmin: Bool { role == "admin" }
}

@MainActor
final class Account: ObservableObject {
    static let server: URL = ProcessInfo.processInfo.environment["CLIPSOUND_SERVER"].flatMap(URL.init(string:))
        ?? URL(string: "https://rooms-production-8e28.up.railway.app")!

    @Published private(set) var user: AccountUser?
    @Published private(set) var clans: [ClanSummary] = []
    @Published var busy = false
    @Published var problem: String?

    private(set) var token: String? {
        didSet {
            if let token { UserDefaults.standard.set(token, forKey: "accountToken") }
            else { UserDefaults.standard.removeObject(forKey: "accountToken") }
        }
    }

    var loggedIn: Bool { user != nil }

    init() {
        token = UserDefaults.standard.string(forKey: "accountToken")
        if token != nil { Task { await refreshMe() } }
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
        token = nil; user = nil; clans = []
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

    /// Mit Invite-Code beitreten. Ergebnis: "member" (schon/direkt drin) oder "requested" (Anfrage gestellt).
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
