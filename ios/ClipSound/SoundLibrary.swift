import AVFoundation
import Foundation

struct Sound: Identifiable, Hashable {
    let url: URL
    var id: String { url.lastPathComponent }
    /// Anzeigename: ohne Endung, Bindestriche/Unterstriche als Leerzeichen
    var title: String {
        url.deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
    }
}

/// Sounds liegen direkt im Dokumente-Ordner der App – so sieht man sie auch in der Dateien-App
/// unter „Auf meinem iPhone › ClipSound“ und kann dort Dateien reinkopieren.
final class SoundLibrary: ObservableObject {
    static let audioExtensions: Set<String> = ["mp3", "wav", "m4a", "aac", "aif", "aiff", "caf", "flac"]

    let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
    @Published private(set) var sounds: [Sound] = []

    init() { reload() }

    func reload() {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.creationDateKey],
                                                                   options: [.skipsHiddenFiles])) ?? []
        func created(_ url: URL) -> Date {
            (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
        }
        let audio = files.filter { Self.audioExtensions.contains($0.pathExtension.lowercased()) }
        // Neueste zuerst, bei gleichem Datum alphabetisch
        let sorted = audio.sorted { a, b in
            let da = created(a), db = created(b)
            return da != db ? da > db : a.lastPathComponent < b.lastPathComponent
        }
        let next = sorted.map(Sound.init(url:))
        if next != sounds { sounds = next }
    }

    /// Importiert Dateien und ganze Ordner aus der Dateien-App. Nur echte Audio-Dateien werden übernommen.
    func importItems(_ urls: [URL]) -> (added: Int, rejected: [String]) {
        var added = 0
        var rejected: [String] = []
        for url in urls {
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            for file in expand(url) {
                guard Self.audioExtensions.contains(file.pathExtension.lowercased()),
                      (try? AVAudioFile(forReading: file)) != nil else {
                    rejected.append(file.lastPathComponent)
                    continue
                }
                do {
                    try FileManager.default.copyItem(at: file, to: uniqueDestination(for: file))
                    added += 1
                } catch {
                    rejected.append(file.lastPathComponent)
                }
            }
        }
        reload()
        return (added, rejected)
    }

    func delete(_ sound: Sound) {
        try? FileManager.default.removeItem(at: sound.url)
        reload()
    }

    private func expand(_ url: URL) -> [URL] {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { return [url] }
        guard isDir.boolValue else { return [url] }
        // In Ordnern werden andere Dateien einfach übersprungen
        let all = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])?.allObjects as? [URL] ?? []
        return all.filter { Self.audioExtensions.contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    private func uniqueDestination(for url: URL) -> URL {
        let base = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension.lowercased()
        var dest = folder.appendingPathComponent("\(base).\(ext)")
        var i = 2
        while FileManager.default.fileExists(atPath: dest.path) {
            dest = folder.appendingPathComponent("\(base) (\(i)).\(ext)")
            i += 1
        }
        return dest
    }
}
