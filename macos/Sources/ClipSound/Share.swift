import CryptoKit
import Foundation

// Sounds gehen von Gerät zu Gerät: Der ClipSound-Server speichert keine Audio-Dateien.
// Braucht jemand einen Sound, bittet der Server ein Gerät, das ihn hat („send“), und reicht die
// Datei direkt weiter. Diese Datei wird von der Mac- und der iOS-App benutzt.

/// sha256 einer Datei – gemerkt nach Pfad, Größe und Änderungsdatum, weil Hashen Zeit kostet.
enum FileHash {
    private static let lock = NSLock()
    private static var cache: [String: (size: Int, date: Date, sha: String)] = [:]

    static func size(of url: URL) -> Int? {
        ((try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.intValue
    }

    static func sha256(of url: URL) -> String? {
        let attrs = try? FileManager.default.attributesOfItem(atPath: url.path)
        guard let size = (attrs?[.size] as? NSNumber)?.intValue else { return nil }
        let date = attrs?[.modificationDate] as? Date ?? .distantPast
        lock.lock()
        let cached = cache[url.path]
        lock.unlock()
        if let cached, cached.size == size, cached.date == date { return cached.sha }
        guard let data = try? Data(contentsOf: url) else { return nil }
        let sha = hex(data)
        lock.lock()
        cache[url.path] = (size, date, sha)
        lock.unlock()
        return sha
    }

    static func hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}

/// Eine Datei auf Anfrage des Servers an PUT /api/relay/<rid> schicken.
enum Relay {
    static func send(_ file: URL, rid: String, server: URL) async {
        var req = URLRequest(url: server.appendingPathComponent("api/relay/\(rid)"))
        req.httpMethod = "PUT"
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        _ = try? await URLSession.shared.upload(for: req, fromFile: file)
    }
}

/// Geräte-Verbindung, solange man angemeldet ist: meldet, welche Clan-Sounds in der eigenen Bibliothek
/// liegen, und schickt einen davon, wenn ein anderes Clan-Mitglied ihn braucht. Bekommt außerdem
/// Live-Updates („im Clan hat sich etwas geändert“). Läuft auf dem Main-Thread (wie Lobby).
final class ClanShare {
    /// Feste ID dieser App-Installation – damit ersetzt der Server eine verwaiste alte Verbindung
    static let deviceID: String = {
        if let id = UserDefaults.standard.string(forKey: "clipsoundDeviceID") { return id }
        let id = UUID().uuidString
        UserDefaults.standard.set(id, forKey: "clipsoundDeviceID")
        return id
    }()

    /// Im Clan hat sich etwas geändert (Sounds, wer online ist, Mitglieder, Session)
    var onClanChanged: ((String) -> Void)?

    private let server: URL
    private var token: String?
    private var socket: URLSessionWebSocketTask?
    private var retry = 0
    private var files: [URL] = []
    private var wanted: [String: Int] = [:]   // sha → Größe (Clan-Sounds, nach denen der Server fragt)
    private var have: [String: URL] = [:]     // sha → Datei auf diesem Gerät
    private var scanRun = 0

    init(server: URL) { self.server = server }

    deinit { socket?.cancel(with: .goingAway, reason: nil) }

    /// Sofort neu verbinden – nach dem Aufwachen oder wenn die App wieder vorne ist (die alte Verbindung ist dann oft tot)
    func reconnectNow() {
        guard token != nil else { return }
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        retry = 0
        connect()
    }

    /// Eigene Dateien sofort neu melden (z. B. gleich nach dem Teilen)
    func refresh() { rescan() }

    func start(token: String) {
        guard token != self.token || socket == nil else { return }
        stop()
        self.token = token
        connect()
    }

    func stop() {
        token = nil
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        wanted = [:]; have = [:]
        scanRun += 1
    }

    /// Die eigenen Sound-Dateien haben sich geändert.
    func filesChanged(_ urls: [URL]) {
        files = urls
        rescan()
    }

    private func connect() {
        guard let token else { return }
        var comps = URLComponents(url: server.appendingPathComponent("ws"), resolvingAgainstBaseURL: false)!
        comps.scheme = server.scheme == "https" ? "wss" : "ws"
        comps.queryItems = [URLQueryItem(name: "share", value: "1")]
        let task = URLSession.shared.webSocketTask(with: comps.url!)
        socket = task
        task.resume()
        send(["type": "auth", "token": token, "device": Self.deviceID]) // Token nicht in die Adresse, die landet in Protokollen
        receive(on: task)
    }

    private func receive(on task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self, task === self.socket else { return }
                switch result {
                case .success(.string(let text)):
                    if let data = text.data(using: .utf8),
                       let msg = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { self.handle(msg) }
                    self.receive(on: task)
                case .success:
                    self.receive(on: task)
                case .failure:
                    self.closed(task.closeCode.rawValue)
                }
            }
        }
    }

    private func closed(_ code: Int) {
        socket = nil
        guard token != nil else { return }
        if code == 4401 || code == 4001 { token = nil; return } // abgemeldet, Konto gelöscht oder durch eine neuere Verbindung ersetzt
        // Netz weg, App im Hintergrund o. Ä.: später neu verbinden
        let delay = min(30, pow(2, Double(retry)))
        retry += 1
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self, self.token != nil, self.socket == nil else { return }
            self.connect()
        }
    }

    private func send(_ msg: [String: Any]) {
        guard let socket, let data = try? JSONSerialization.data(withJSONObject: msg),
              let text = String(data: data, encoding: .utf8) else { return }
        socket.send(.string(text)) { _ in }
    }

    private func handle(_ msg: [String: Any]) {
        switch msg["type"] as? String {
        case "ready":
            retry = 0
        case "want":
            wanted = [:]
            for s in msg["sounds"] as? [[String: Any]] ?? [] {
                if let id = s["id"] as? String, let size = s["size"] as? Int { wanted[id] = size }
            }
            rescan()
        case "clan":
            if let id = msg["clanId"] as? String { onClanChanged?(id) }
        case "send":
            guard let rid = msg["rid"] as? String, let id = msg["id"] as? String else { return }
            guard let file = have[id] else { send(["type": "nosend", "rid": rid]); return }
            let server = server
            Task.detached(priority: .utility) { await Relay.send(file, rid: rid, server: server) }
        default:
            break
        }
    }

    /// In den eigenen Dateien nach den gesuchten Clan-Sounds schauen. Gehasht werden nur Dateien,
    /// deren Größe zu einem gesuchten Sound passt – das geht auch bei großen Bibliotheken schnell.
    private func rescan() {
        guard socket != nil else { return }
        scanRun += 1
        let run = scanRun, files = files, wanted = wanted
        let sizes = Set(wanted.values)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var found: [String: URL] = [:]
            for url in files {
                guard let size = FileHash.size(of: url), sizes.contains(size),
                      let sha = FileHash.sha256(of: url), wanted[sha] != nil else { continue }
                found[sha] = url
            }
            DispatchQueue.main.async {
                guard let self, run == self.scanRun else { return }
                self.have = found
                self.send(["type": "have", "ids": Array(found.keys)])
            }
        }
    }
}
