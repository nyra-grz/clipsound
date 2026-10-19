import AppKit
import Combine

/// Bilder für Sounds: liegen verkleinert als PNG in <Einstellungen>/Bilder/<Datei-ID>.png, neben keybinds.json.
/// Nur auf diesem Mac – in Lobbys und Clans gehen weiter nur die Sounds selbst.
final class SoundImages: ObservableObject {
    @Published private(set) var images: [String: NSImage] = [:]
    private let folder: URL

    init(folder: URL) {
        self.folder = folder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
        for file in files where file.pathExtension == "png" {
            if let image = NSImage(contentsOf: file) { images[file.deletingPathExtension().lastPathComponent] = image }
        }
    }

    private func file(for id: String) -> URL { folder.appendingPathComponent(id + ".png") }

    /// Bild setzen (auf höchstens 600 px verkleinert). false = Bild ließ sich nicht verarbeiten
    @discardableResult
    func set(_ source: NSImage, for id: String) -> Bool {
        guard let png = Self.scaledPNG(source) else { return false }
        do { try png.write(to: file(for: id), options: .atomic) } catch { return false }
        images[id] = NSImage(data: png)
        return true
    }

    @discardableResult
    func set(contentsOf url: URL, for id: String) -> Bool {
        guard let image = NSImage(contentsOf: url) else { return false }
        return set(image, for: id)
    }

    func remove(_ id: String) {
        try? FileManager.default.removeItem(at: file(for: id))
        images[id] = nil
    }

    private static func scaledPNG(_ source: NSImage, maxSide: CGFloat = 600) -> Data? {
        guard let cg = source.cgImage(forProposedRect: nil, context: nil, hints: nil), cg.width > 0, cg.height > 0 else { return nil }
        let scale = min(1, maxSide / CGFloat(max(cg.width, cg.height)))
        let width = max(1, Int(CGFloat(cg.width) * scale)), height = max(1, Int(CGFloat(cg.height) * scale))
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let out = ctx.makeImage() else { return nil }
        return NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:])
    }
}
