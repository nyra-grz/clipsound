import AVFoundation
import AppKit

struct Sound: Identifiable, Hashable {
    let url: URL
    var id: String { url.lastPathComponent }
    /// Anzeigename: ohne Endung, Bindestriche/Unterstriche als Leerzeichen
    var title: String {
        url.deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: "[-_]+", with: " ", options: .regularExpression)
    }
}

/// Verwaltet den Sound-Ordner der App (startet leer).
final class SoundLibrary: ObservableObject {
    static let audioExtensions: Set<String> = ["mp3", "wav", "ogg", "opus", "m4a", "aac", "webm", "flac", "aif", "aiff", "caf"]

    let folder: URL
    @Published private(set) var sounds: [Sound] = []

    /// ~/Library/Application Support/ClipSound – übernimmt einmalig den alten „Meme Soundboard“-Ordner
    static let supportFolder: URL = {
        let fm = FileManager.default
        let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let folder = support.appendingPathComponent("ClipSound", isDirectory: true)
        let old = support.appendingPathComponent("Meme Soundboard", isDirectory: true)
        if !fm.fileExists(atPath: folder.path), fm.fileExists(atPath: old.path) {
            try? fm.moveItem(at: old, to: folder)
        }
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }()

    /// Sounds liegen sichtbar auf dem Schreibtisch unter „Sounds“
    static let desktopFolder: URL = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Sounds", isDirectory: true)

    /// Hier liegen Einstellungen wie keybinds.json (nicht im Sound-Ordner)
    let settingsFolder: URL

    init(folder: URL? = nil) {
        if let folder {
            self.folder = folder
            settingsFolder = folder.deletingLastPathComponent()
        } else {
            self.folder = Self.desktopFolder
            settingsFolder = Self.supportFolder
        }
        try? FileManager.default.createDirectory(at: self.folder, withIntermediateDirectories: true)
        if folder == nil { Self.moveOldSounds(to: self.folder) }
        reload()
    }

    /// Bis 1.3 lagen die Sounds in ~/Library/Application Support/ClipSound/Sounds. Sie werden verschoben,
    /// nie gelöscht: Gleicher Name mit gleichem Inhalt bleibt als Kopie im alten Ordner,
    /// gleicher Name mit anderem Inhalt kommt als „Name (2)“ dazu.
    private static func moveOldSounds(to dest: URL) {
        let fm = FileManager.default
        let old = supportFolder.appendingPathComponent("Sounds", isDirectory: true)
        guard let files = try? fm.contentsOfDirectory(at: old, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else { return }
        for file in files where audioExtensions.contains(file.pathExtension.lowercased()) {
            var target = dest.appendingPathComponent(file.lastPathComponent)
            if fm.fileExists(atPath: target.path) {
                if fm.contentsEqual(atPath: file.path, andPath: target.path) { continue }
                let base = file.deletingPathExtension().lastPathComponent, ext = file.pathExtension
                var i = 2
                repeat { target = dest.appendingPathComponent("\(base) (\(i)).\(ext)"); i += 1 } while fm.fileExists(atPath: target.path)
            }
            try? fm.moveItem(at: file, to: target)
        }
        // leeren alten Ordner wegräumen (nur wenn wirklich nichts mehr drin ist)
        if (try? fm.contentsOfDirectory(atPath: old.path))?.isEmpty == true { try? fm.removeItem(at: old) }
    }

    func reload() {
        let fm = FileManager.default
        let keys: [URLResourceKey] = [.creationDateKey]
        let files = (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])) ?? []
        func created(_ url: URL) -> Date {
            (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
        }
        let audio = files.filter { Self.audioExtensions.contains($0.pathExtension.lowercased()) }
        // Neueste zuerst, bei gleichem Datum alphabetisch
        let sorted = audio.sorted { a, b in
            let da = created(a), db = created(b)
            return da != db ? da > db : a.lastPathComponent < b.lastPathComponent
        }
        sounds = sorted.map(Sound.init(url:))
    }

    /// Importiert Dateien und ganze Ordner. Nur echte Audio-Dateien werden übernommen.
    @discardableResult
    func importItems(_ urls: [URL]) -> (added: Int, rejected: [String]) {
        var added = 0
        var rejected: [String] = []
        for url in expand(urls) {
            guard Self.audioExtensions.contains(url.pathExtension.lowercased()),
                  (try? AVAudioFile(forReading: url)) != nil else {
                rejected.append(url.lastPathComponent)
                continue
            }
            do {
                try FileManager.default.copyItem(at: url, to: uniqueDestination(for: url))
                added += 1
            } catch {
                rejected.append(url.lastPathComponent)
            }
        }
        reload()
        return (added, rejected)
    }

    func delete(_ sound: Sound) {
        try? FileManager.default.trashItem(at: sound.url, resultingItemURL: nil)
        reload()
    }

    func revealInFinder(_ sound: Sound? = nil) {
        if let sound {
            NSWorkspace.shared.activateFileViewerSelecting([sound.url])
        } else {
            NSWorkspace.shared.open(folder)
        }
    }

    // Ordner rekursiv aufklappen
    private func expand(_ urls: [URL]) -> [URL] {
        var result: [URL] = []
        for url in urls {
            var isDir: ObjCBool = false
            guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { continue }
            if isDir.boolValue {
                // In Ordnern werden andere Dateien (Bilder, Text …) einfach übersprungen
                let all = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])?.allObjects as? [URL] ?? []
                result += all
                    .filter { Self.audioExtensions.contains($0.pathExtension.lowercased()) }
                    .sorted { $0.lastPathComponent < $1.lastPathComponent }
            } else {
                result.append(url)
            }
        }
        return result
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
