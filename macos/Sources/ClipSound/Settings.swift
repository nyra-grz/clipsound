import SwiftUI
import AppKit
import UniformTypeIdentifiers

enum AppSettings {
    static let keepInMenuBarKey = "keepInMenuBar"
    /// Standard: an – so war es seit 1.5 (Fenster zu beendet nicht)
    static var keepInMenuBar: Bool { UserDefaults.standard.object(forKey: keepInMenuBarKey) as? Bool ?? true }
}

/// Eigenes App-Icon: liegt als PNG in App Support und ersetzt beim Start das Dock-Symbol
final class CustomIcon: ObservableObject {
    static let shared = CustomIcon()
    private let file = SoundLibrary.supportFolder.appendingPathComponent("AppIcon.png")
    @Published private(set) var image: NSImage?

    private init() { image = NSImage(contentsOf: file) }

    func apply() { NSApp.applicationIconImage = image } // nil = Icon aus dem Bundle

    @discardableResult
    func set(from url: URL) -> Bool {
        guard let source = NSImage(contentsOf: url), let png = Self.squarePNG(source) else { return false }
        do { try png.write(to: file, options: .atomic) } catch { return false }
        image = NSImage(data: png)
        apply()
        return true
    }

    func reset() {
        try? FileManager.default.removeItem(at: file)
        image = nil
        apply()
    }

    func choose() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.message = "Bild für das ClipSound-Icon auswählen"
        if panel.runModal() == .OK, let url = panel.url { set(from: url) }
    }

    /// Mittig quadratisch zuschneiden und wie ein macOS-Icon abrunden (824 px im 1024er-Raster)
    private static func squarePNG(_ source: NSImage) -> Data? {
        guard let cg = source.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        let side = min(cg.width, cg.height)
        guard let cropped = cg.cropping(to: CGRect(x: (cg.width - side) / 2, y: (cg.height - side) / 2, width: side, height: side)) else { return nil }
        let size = 1024
        guard let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        let rect = CGRect(x: 100, y: 100, width: 824, height: 824)
        ctx.addPath(CGPath(roundedRect: rect, cornerWidth: 185, cornerHeight: 185, transform: nil))
        ctx.clip()
        ctx.draw(cropped, in: rect)
        guard let out = ctx.makeImage() else { return nil }
        return NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:])
    }
}

struct SettingsView: View {
    @AppStorage(AppSettings.keepInMenuBarKey) private var keepInMenuBar = true
    @ObservedObject private var icon = CustomIcon.shared

    var body: some View {
        Form {
            Section("Fenster") {
                Toggle("Beim Schließen in der Menüleiste weiterlaufen", isOn: $keepInMenuBar)
                Text(keepInMenuBar
                     ? "Der rote Knopf schließt nur das Fenster. Globale Tastenkürzel (mit ⌥ oder ⌃) gehen weiter, ClipSound bleibt als Symbol oben in der Menüleiste. Beenden mit ⌘Q oder über das Symbol."
                     : "Der rote Knopf beendet ClipSound. Danach gehen auch die Tastenkürzel nicht mehr.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Section("App-Icon") {
                HStack(spacing: 14) {
                    Image(nsImage: icon.image ?? NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 56, height: 56)
                    VStack(alignment: .leading, spacing: 6) {
                        Text(icon.image == nil ? "Standard-Icon" : "Eigenes Icon")
                        HStack {
                            Button("Bild auswählen …") { icon.choose() }
                            if icon.image != nil { Button("Zurücksetzen") { icon.reset() } }
                        }
                    }
                }
                .dropDestination(for: URL.self) { urls, _ in urls.first.map { icon.set(from: $0) } ?? false }
                Text("Gilt fürs Dock, solange ClipSound läuft. Bild hier hineinziehen geht auch.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .fixedSize()
    }
}
