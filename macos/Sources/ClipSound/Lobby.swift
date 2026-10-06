import AVFoundation
import CryptoKit
import Foundation

// Lobby: gemeinsam abspielen über den ClipSound-Server (cloud/server.js).
// Wer die Lobby öffnet (Host), bringt seine Sounds mit; Gäste sehen sie und können sie abspielen.
// Jeder gedrückte Sound startet bei allen zur gleichen Zeit. Behalten nur, wenn beide zustimmen.
// Diese Datei wird von der Mac- und der iOS-App benutzt.

struct LobbySound: Identifiable, Hashable {
    let id: String      // sha256 der Datei
    let name: String    // Dateiname mit Endung
    let size: Int
    /// Datei auf diesem Gerät (beim Gast im Cache, sobald geladen)
    var local: Sound?

    var title: String {
        (name as NSString).deletingPathExtension
            .replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
    }
}

struct LobbyMember: Identifiable, Hashable, Decodable {
    let id: String
    let name: String
    let host: Bool
}

/// Eine offene Frage an mich: Gast möchte einen Sound behalten (an den Host) oder Host bietet einen an (an den Gast)
struct LobbyRequest: Identifiable, Hashable {
    enum Kind { case asked, offered }
    let id: String      // req vom Server
    let kind: Kind
    let soundID: String
    let soundName: String
    let by: String

    var soundTitle: String {
        (soundName as NSString).deletingPathExtension
            .replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
    }
}

final class Lobby: ObservableObject {
    enum Phase: Equatable { case off, connecting, open, reconnecting }

    static let defaultServer = URL(string: "https://rooms-production-8e28.up.railway.app")!

    @Published private(set) var phase: Phase = .off
    @Published private(set) var code = ""
    @Published private(set) var isHost = false
    @Published private(set) var hostName = ""
    @Published private(set) var members: [LobbyMember] = []
    @Published private(set) var me = ""
    /// Gast: Sounds des Hosts (local == nil heißt: lädt noch)
    @Published private(set) var sounds: [LobbySound] = []
    /// Host: wie viele eigene Sounds schon in der Lobby sind
    @Published private(set) var uploaded: (done: Int, total: Int)?
    @Published var requests: [LobbyRequest] = []
    /// Kurze Meldung für die Oberfläche (z. B. „Daniel hat gestoppt“)
    @Published var note: String?
    /// Fehler, der die Lobby beendet hat
    @Published var problem: String?

    @Published var name: String {
        didSet { UserDefaults.standard.set(name, forKey: "lobbyName") }
    }

    /// Abspielen zur Startzeit (Verzögerung in Sekunden ab jetzt)
    var onPlay: ((Sound, TimeInterval) -> Void)?
    var onStop: (() -> Void)?
    /// Gast: Zustimmung da – Datei mit Originalnamen in die eigene Bibliothek übernehmen
    var onReceive: ((URL) -> Void)?

    let server: URL
    private let session = URLSession(configuration: .default)
    private var socket: URLSessionWebSocketTask?
    private var hostToken: String?
    private var retry = 0
    private var offset: TimeInterval = 0   // Serverzeit − meine Zeit
    private var bestRTT = TimeInterval.infinity
    private var pingTimer: Timer?
    private var syncTask: Task<Void, Never>?

    // Host: Hash pro Datei (Pfad → (Größe, Änderungsdatum, sha))
    private var hashCache: [String: (Int, Date, String)] = [:]
    private var hostFiles: [String: Sound] = [:]  // sha → eigene Datei
    private var hostIDs: [String: String] = [:]   // Datei-ID → sha
    private var pendingFiles: [Sound] = []

    private let cache: URL

    init(server: URL? = nil) {
        let env = ProcessInfo.processInfo.environment["CLIPSOUND_SERVER"].flatMap(URL.init(string:))
        self.server = server ?? env ?? Self.defaultServer
        name = UserDefaults.standard.string(forKey: "lobbyName") ?? ""
        cache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ClipSound Lobby", isDirectory: true)
    }

    var active: Bool { phase != .off }
    var displayCode: String { code }
    var guests: [LobbyMember] { members.filter { !$0.host && $0.id != me } }
    private var personName: String {
        let n = name.trimmingCharacters(in: .whitespaces)
        #if os(macOS)
        return n.isEmpty ? Host.current().localizedName ?? "Gast" : n
        #else
        return n.isEmpty ? "Gast" : n
        #endif
    }

    // MARK: Öffnen, beitreten, verlassen

    /// Lobby mit den eigenen Sounds öffnen
    func open(with files: [Sound]) {
        guard phase == .off else { return }
        phase = .connecting
        problem = nil
        Task {
            do {
                var req = URLRequest(url: server.appendingPathComponent("api/lobbies"))
                req.httpMethod = "POST"
                req.httpBody = try JSONSerialization.data(withJSONObject: ["name": personName])
                let (data, resp) = try await session.data(for: req)
                guard (resp as? HTTPURLResponse)?.statusCode == 200,
                      let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let code = obj["code"] as? String, let token = obj["hostToken"] as? String
                else { throw LobbyError.message(Self.errorText(data) ?? "Lobby konnte nicht geöffnet werden.") }
                await MainActor.run {
                    self.code = code
                    self.hostToken = token
                    self.isHost = true
                    self.connect()
                    self.hostLibraryChanged(files)
                }
            } catch {
                await MainActor.run { self.fail(error) }
            }
        }
    }

    /// Mit Code beitreten (Groß-/Kleinschreibung egal)
    func join(_ raw: String) {
        let code = String(raw.uppercased().filter { $0.isLetter || $0.isNumber }.prefix(5))
        guard phase == .off else { return }
        guard code.count == 5 else { problem = "Der Code hat 5 Zeichen."; return }
        problem = nil
        self.code = code
        isHost = false
        hostToken = nil
        phase = .connecting
        connect()
    }

    func leave() {
        let wasHost = isHost
        phase = .off
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        pingTimer?.invalidate(); pingTimer = nil
        syncTask?.cancel(); syncTask = nil
        code = ""; isHost = false; hostToken = nil; hostName = ""
        members = []; sounds = []; requests = []; uploaded = nil
        hostFiles = [:]; hostIDs = [:]
        onStop?()
        if !wasHost { try? FileManager.default.removeItem(at: cache) } // fremde Sounds nicht liegen lassen
    }

    private func fail(_ error: Error) {
        let text = (error as? LobbyError)?.text ?? "Keine Verbindung zum ClipSound-Server."
        leave()
        problem = text
    }

    // MARK: Abspielen

    /// Host: Sound aus der eigenen Bibliothek. Gibt false zurück, wenn er (noch) nicht in der Lobby ist.
    func play(own sound: Sound) -> Bool {
        // erst wenn die Datei auf dem Server ist (der Server listet nur fertige Sounds)
        guard phase == .open, let sha = hostIDs[sound.id], sounds.contains(where: { $0.id == sha }) else { return false }
        send(["type": "play", "id": sha])
        return true
    }

    /// Gast: Sound des Hosts
    func play(_ sound: LobbySound) {
        guard sound.local != nil else { note = "„\(sound.title)“ lädt noch …"; return }
        guard phase == .open else { note = "Keine Verbindung – verbinde neu …"; return }
        send(["type": "play", "id": sound.id])
    }

    func stopAll() { send(["type": "stop"]) }

    // MARK: Sounds weitergeben

    /// Gast fragt den Host, ob er den Sound behalten darf
    func ask(_ sound: LobbySound) {
        send(["type": "ask", "id": sound.id])
        note = "Gefragt, ob du „\(sound.title)“ behalten darfst …"
    }

    /// Host bietet einem Gast einen eigenen Sound an
    func offer(_ sound: Sound, to member: LobbyMember) {
        guard let sha = hostIDs[sound.id] else { note = "„\(sound.title)“ ist noch nicht in der Lobby."; return }
        send(["type": "offer", "id": sha, "to": member.id])
        note = "„\(sound.title)“ an \(member.name) angeboten …"
    }

    func answer(_ request: LobbyRequest, ok: Bool) {
        requests.removeAll { $0.id == request.id }
        send(["type": "answer", "req": request.id, "ok": ok])
    }

    // MARK: Host: eigene Sounds in die Lobby bringen

    /// Bibliothek hat sich geändert (oder Lobby gerade offen) – Liste neu schicken, fehlende Dateien hochladen
    func hostLibraryChanged(_ files: [Sound]) {
        guard isHost, active else { return }
        pendingFiles = files
        syncTask?.cancel()
        syncTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 300_000_000) // mehrere Änderungen zusammenfassen
            guard !Task.isCancelled else { return }
            await self?.syncHost()
        }
    }

    private func syncHost() async {
        guard let token = hostToken else { return }
        let files = pendingFiles
        // Hashen kostet Zeit, also im Hintergrund und mit Cache
        let cacheSnapshot = hashCache
        let hashed: [(Sound, String, Int, Date)] = await Task.detached(priority: .utility) {
            files.compactMap { sound in
                let attrs = try? FileManager.default.attributesOfItem(atPath: sound.url.path)
                let size = (attrs?[.size] as? NSNumber)?.intValue ?? 0
                let date = attrs?[.modificationDate] as? Date ?? .distantPast
                if let c = cacheSnapshot[sound.url.path], c.0 == size, c.1 == date { return (sound, c.2, size, date) }
                guard let data = try? Data(contentsOf: sound.url) else { return nil }
                let sha = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
                return (sound, sha, size, date)
            }
        }.value
        guard !Task.isCancelled else { return }

        await MainActor.run {
            for (sound, sha, size, date) in hashed {
                hashCache[sound.url.path] = (size, date, sha)
                hostFiles[sha] = sound
                hostIDs[sound.id] = sha
            }
        }
        let list = hashed.map { ["id": $0.1, "name": $0.0.url.lastPathComponent, "size": $0.2] as [String: Any] }
        do {
            var req = URLRequest(url: server.appendingPathComponent("api/lobbies/\(code)/sounds"))
            req.httpMethod = "PUT"
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            req.httpBody = try JSONSerialization.data(withJSONObject: ["sounds": list])
            let (data, resp) = try await session.data(for: req)
            guard (resp as? HTTPURLResponse)?.statusCode == 200,
                  let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
            let missing = obj["missing"] as? [String] ?? []
            let skipped = obj["skipped"] as? [String] ?? []
            let total = list.count - skipped.count
            await MainActor.run {
                self.uploaded = (total - missing.count, total)
                if !skipped.isEmpty { self.note = "\(skipped.count) Sound(s) zu groß für die Lobby (max. 150 MB gesamt)." }
            }
            for sha in missing {
                guard !Task.isCancelled, let sound = await MainActor.run(body: { self.hostFiles[sha] }) else { return }
                var up = URLRequest(url: server.appendingPathComponent("api/lobbies/\(code)/blobs/\(sha)"))
                up.httpMethod = "PUT"
                up.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
                let (_, r) = try await session.upload(for: up, fromFile: sound.url)
                if (r as? HTTPURLResponse)?.statusCode == 200 {
                    await MainActor.run { if let u = self.uploaded { self.uploaded = (u.done + 1, u.total) } }
                }
            }
        } catch {
            await MainActor.run { self.note = "Hochladen hat nicht geklappt – versuche es gleich nochmal." }
        }
    }

    // MARK: Verbindung

    private func connect() {
        var comps = URLComponents(url: server.appendingPathComponent("ws"), resolvingAgainstBaseURL: false)!
        comps.scheme = server.scheme == "https" ? "wss" : "ws"
        comps.queryItems = [URLQueryItem(name: "lobby", value: code), URLQueryItem(name: "name", value: personName)]
        if let hostToken { comps.queryItems?.append(URLQueryItem(name: "token", value: hostToken)) }
        let task = session.webSocketTask(with: comps.url!)
        socket = task
        task.resume()
        receive(on: task)
        bestRTT = .infinity
        for i in 0..<5 {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4 + Double(i) * 0.3) { [weak self] in self?.ping() }
        }
        pingTimer?.invalidate()
        pingTimer = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in self?.ping() }
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
        guard phase != .off else { return }
        switch code {
        case 4404: fail(LobbyError.message("Diese Lobby gibt es nicht (mehr)."))
        case 4401: fail(LobbyError.message("Lobby-Zugang ungültig."))
        case 4409: fail(LobbyError.message("Die Lobby ist voll."))
        case 4410: fail(LobbyError.message(problem ?? "Die Lobby wurde geschlossen."))
        case 4429: fail(LobbyError.message("Zu viele falsche Codes – warte ein paar Minuten."))
        default:
            // Netz weg o. Ä.: automatisch neu verbinden
            if phase == .connecting && retry >= 2 { fail(LobbyError.message("Keine Verbindung zum ClipSound-Server.")); return }
            phase = .reconnecting
            let delay = min(10, 0.5 * pow(2, Double(retry)))
            retry += 1
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                guard let self, self.phase == .reconnecting else { return }
                self.connect()
            }
        }
    }

    private func send(_ msg: [String: Any]) {
        guard let socket, let data = try? JSONSerialization.data(withJSONObject: msg),
              let text = String(data: data, encoding: .utf8) else { return }
        socket.send(.string(text)) { _ in }
    }

    private func ping() {
        send(["type": "ping", "t": Date().timeIntervalSince1970 * 1000])
    }

    private func handle(_ msg: [String: Any]) {
        switch msg["type"] as? String {
        case "hello":
            phase = .open
            retry = 0
            me = msg["you"] as? String ?? ""
            hostName = msg["host"] as? String ?? ""
            if let t = msg["serverTime"] as? Double, bestRTT == .infinity { offset = t / 1000 - Date().timeIntervalSince1970 }
            setMembers(msg["members"])
            setSounds(msg["sounds"])
            if isHost { hostLibraryChanged(pendingFiles) } // nach Neuverbindung Liste auffrischen
        case "sounds":
            setSounds(msg["sounds"])
        case "members":
            setMembers(msg["members"])
        case "pong":
            guard let t = msg["t"] as? Double, let st = msg["serverTime"] as? Double else { return }
            let now = Date().timeIntervalSince1970
            let rtt = now - t / 1000
            if rtt < bestRTT { bestRTT = rtt; offset = st / 1000 - (t / 1000 + rtt / 2) }
        case "play":
            guard let id = msg["id"] as? String, let at = msg["at"] as? Double else { return }
            let local = isHost ? hostFiles[id] : sounds.first { $0.id == id }?.local
            guard let local else { return }
            let delay = max(0, at / 1000 - offset - Date().timeIntervalSince1970)
            onPlay?(local, delay)
        case "stop":
            onStop?()
            if msg["from"] as? String != me, let by = msg["by"] as? String { note = "\(by) hat gestoppt" }
        case "asked", "offered":
            guard let req = msg["req"] as? String, let id = msg["id"] as? String,
                  let name = msg["name"] as? String, let by = msg["by"] as? String else { return }
            requests.append(LobbyRequest(id: req, kind: msg["type"] as? String == "asked" ? .asked : .offered,
                                         soundID: id, soundName: name, by: by))
        case "answered":
            answered(msg)
        case "closed":
            problem = msg["reason"] as? String
        default:
            break
        }
    }

    private func answered(_ msg: [String: Any]) {
        let ok = msg["ok"] as? Bool ?? false
        let name = (msg["name"] as? String).map {
            ($0 as NSString).deletingPathExtension.replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
        } ?? "Sound"
        guard !isHost else {
            if ok { note = "„\(name)“ wurde weitergegeben." }
            return
        }
        guard ok else {
            note = msg["kind"] as? String == "ask" ? (msg["reason"] as? String ?? "\(hostName) möchte „\(name)“ nicht hergeben.") : nil
            return
        }
        // Beide haben zugestimmt → Kopie mit Originalnamen in die eigene Bibliothek
        guard let id = msg["id"] as? String, let sound = sounds.first(where: { $0.id == id }) else { return }
        if let file = sound.local, let data = try? Data(contentsOf: file.url) { saveReceived(sound, data); return }
        // Schon zugestimmt, aber noch nicht fertig geladen: jetzt direkt holen
        let url = server.appendingPathComponent("api/lobbies/\(code)/blobs/\(sound.id)")
        Task {
            if let (data, resp) = try? await session.data(from: url), (resp as? HTTPURLResponse)?.statusCode == 200,
               SHA256.hash(data: data).map({ String(format: "%02x", $0) }).joined() == sound.id {
                await MainActor.run { self.saveReceived(sound, data) }
            } else {
                await MainActor.run { self.note = "„\(sound.title)“ konnte nicht gespeichert werden." }
            }
        }
    }

    private func saveReceived(_ sound: LobbySound, _ data: Data) {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let copy = dir.appendingPathComponent(sound.name)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try data.write(to: copy)
            onReceive?(copy)
            note = "„\(sound.title)“ ist jetzt in deinen Sounds."
        } catch {
            note = "„\(sound.title)“ konnte nicht gespeichert werden."
        }
        try? FileManager.default.removeItem(at: dir)
    }

    private func setMembers(_ raw: Any?) {
        guard let raw, let data = try? JSONSerialization.data(withJSONObject: raw),
              let list = try? JSONDecoder().decode([LobbyMember].self, from: data) else { return }
        members = list
    }

    private func setSounds(_ raw: Any?) {
        guard let list = raw as? [[String: Any]] else { return }
        let next = list.compactMap { s -> LobbySound? in
            guard let id = s["id"] as? String, let name = s["name"] as? String else { return nil }
            var sound = LobbySound(id: id, name: name, size: s["size"] as? Int ?? 0)
            sound.local = isHost ? hostFiles[id] : cachedFile(for: sound)
            return sound
        }
        sounds = next
        if !isHost { download(next.filter { $0.local == nil }) }
    }

    // MARK: Gast: Dateien vorladen, damit der Start sofort klappt

    private func cacheURL(for sound: LobbySound) -> URL {
        let ext = (sound.name as NSString).pathExtension.lowercased()
        return cache.appendingPathComponent(sound.id).appendingPathExtension(ext.isEmpty ? "mp3" : ext)
    }

    private func cachedFile(for sound: LobbySound) -> Sound? {
        let url = cacheURL(for: sound)
        return FileManager.default.fileExists(atPath: url.path) ? Sound(url: url) : nil
    }

    private var downloading: Set<String> = []

    private func download(_ list: [LobbySound]) {
        try? FileManager.default.createDirectory(at: cache, withIntermediateDirectories: true)
        let todo = list.filter { !downloading.contains($0.id) }
        todo.forEach { downloading.insert($0.id) }
        let code = self.code
        Task {
            // drei gleichzeitig reicht und schont den Server
            await withTaskGroup(of: Void.self) { group in
                var it = todo.makeIterator()
                func next() -> Bool {
                    guard let sound = it.next() else { return false }
                    group.addTask { await self.fetch(sound, code: code) }
                    return true
                }
                for _ in 0..<3 where next() {}
                while await group.next() != nil { _ = next() }
            }
        }
    }

    private func fetch(_ sound: LobbySound, code: String) async {
        defer { DispatchQueue.main.async { self.downloading.remove(sound.id) } }
        let url = server.appendingPathComponent("api/lobbies/\(code)/blobs/\(sound.id)")
        guard let (tmp, resp) = try? await session.download(from: url),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let data = try? Data(contentsOf: tmp),
              SHA256.hash(data: data).map({ String(format: "%02x", $0) }).joined() == sound.id else { return }
        let dest = cacheURL(for: sound)
        try? data.write(to: dest, options: .atomic)
        await MainActor.run {
            guard self.code == code, let i = self.sounds.firstIndex(where: { $0.id == sound.id }) else { return }
            self.sounds[i].local = Sound(url: dest)
        }
    }

    private static func errorText(_ data: Data) -> String? {
        (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
    }
}

enum LobbyError: Error {
    case message(String)
    var text: String { switch self { case .message(let t): t } }
}
