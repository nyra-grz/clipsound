import AppKit

/// Prüft GitHub auf eine neuere Version, lädt sie und ersetzt die laufende App.
@MainActor
final class Updater: ObservableObject {
    static let repo = "nyra-grz/clipsound"
    static let assetName = "ClipSound-macOS.zip"

    struct Release: Equatable {
        let version: String
        let notes: String
        let download: URL
        let page: URL
    }

    enum State: Equatable { case idle, checking, downloading, failed(String), upToDate }

    @Published var available: Release?
    @Published var state: State = .idle

    /// --pretend-version tut so, als wäre eine ältere Version installiert (zum Testen)
    var currentVersion: String {
        LaunchArgs.value("--pretend-version")
            ?? Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"
    }

    func check(userInitiated: Bool = false) async {
        state = .checking
        do {
            var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, _) = try await URLSession.shared.data(for: request)
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = json["tag_name"] as? String,
                  let page = (json["html_url"] as? String).flatMap(URL.init(string:)),
                  let assets = json["assets"] as? [[String: Any]],
                  let asset = assets.first(where: { $0["name"] as? String == Self.assetName }),
                  let download = (asset["browser_download_url"] as? String).flatMap(URL.init(string:)) else {
                state = userInitiated ? .failed("Keine Update-Infos gefunden.") : .idle
                return
            }
            let version = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            if Self.isNewer(version, than: currentVersion) {
                available = Release(version: version, notes: json["body"] as? String ?? "", download: download, page: page)
                state = .idle
            } else {
                available = nil
                state = userInitiated ? .upToDate : .idle
            }
        } catch {
            state = userInitiated ? .failed("Keine Verbindung zu GitHub.") : .idle
        }
    }

    /// Lädt das Update, tauscht die App aus und startet sie neu.
    func install() async {
        guard let release = available else { return }
        state = .downloading
        do {
            let fm = FileManager.default
            let work = fm.temporaryDirectory.appendingPathComponent("ClipSound-Update-\(UUID().uuidString)")
            try fm.createDirectory(at: work, withIntermediateDirectories: true)

            let (zip, _) = try await URLSession.shared.download(from: release.download)
            try Self.run("/usr/bin/ditto", ["-x", "-k", zip.path, work.path])
            let newApp = work.appendingPathComponent("ClipSound.app")
            guard let bundle = Bundle(url: newApp), bundle.bundleIdentifier == Bundle.main.bundleIdentifier else {
                throw UpdateError("Das heruntergeladene Update ist beschädigt.")
            }

            let current = Bundle.main.bundleURL
            guard fm.isWritableFile(atPath: current.deletingLastPathComponent().path) else {
                throw UpdateError("Keine Schreibrechte für \(current.deletingLastPathComponent().path).")
            }
            // Laufende App gegen die neue tauschen (geht auch, während sie läuft)
            _ = try fm.replaceItemAt(current, withItemAt: newApp)

            // Nach dem Beenden neu öffnen
            let relaunch = Process()
            relaunch.executableURL = URL(fileURLWithPath: "/bin/sh")
            relaunch.arguments = ["-c", "sleep 1; /usr/bin/open \"\(current.path)\""]
            try relaunch.run()
            NSApp.terminate(nil)
        } catch {
            let message = (error as? UpdateError)?.message ?? error.localizedDescription
            state = .failed("Update fehlgeschlagen: \(message)")
        }
    }

    func openReleasePage() {
        NSWorkspace.shared.open(available?.page ?? URL(string: "https://github.com/\(Self.repo)/releases/latest")!)
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    private static func run(_ tool: String, _ args: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        try p.run()
        p.waitUntilExit()
        if p.terminationStatus != 0 { throw UpdateError("\(tool) ist fehlgeschlagen.") }
    }

    struct UpdateError: Error { let message: String; init(_ m: String) { message = m } }
}
