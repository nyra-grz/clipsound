import ImageIO
import Photos
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

/// Eigenes App-Icon (wie in der Mac-, Windows- und Linux-App). iOS lässt Apps ihr Symbol auf dem Home-Bildschirm
/// nicht frei ändern – das Bild kommt über die Kurzbefehle-App dorthin („App öffnen“ → „Zum Home-Bildschirm“).
@MainActor
final class CustomIcon: ObservableObject {
    static let shared = CustomIcon()
    /// App Support statt Dokumente: der Dokumente-Ordner ist in der Dateien-App der Sound-Ordner
    private let file: URL
    @Published private(set) var image: UIImage?

    private init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        try? FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        file = support.appendingPathComponent("AppIcon.png")
        image = UIImage(contentsOfFile: file.path)
    }

    /// false = kein lesbares Bild
    @discardableResult
    func set(_ data: Data) -> Bool {
        guard let png = Self.squarePNG(data) else { return false }
        do { try png.write(to: file, options: .atomic) } catch { return false }
        image = UIImage(data: png)
        return true
    }

    func reset() {
        try? FileManager.default.removeItem(at: file)
        image = nil
    }

    enum SaveResult { case saved, denied, failed }

    /// Für die Kurzbefehle-App: Bild in die Fotos legen (nur hinzufügen, ClipSound sieht deine Fotos nicht)
    func saveToPhotos() async -> SaveResult {
        guard let data = try? Data(contentsOf: file) else { return .failed }
        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else { return .denied }
        do {
            try await PHPhotoLibrary.shared().performChanges {
                PHAssetCreationRequest.forAsset().addResource(with: .photo, data: data, options: nil)
            }
            return .saved
        } catch { return .failed }
    }

    /// Mittig quadratisch zuschneiden, 1024 × 1024 (Ecken rundet iOS selbst). Liest auch HEIC von der Kamera
    /// und große Fotos, ohne sie ganz in den Speicher zu laden.
    private static func squarePNG(_ data: Data) -> Data? {
        let options = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                       kCGImageSourceCreateThumbnailWithTransform: true, // Hochformat-Fotos richtig herum
                       kCGImageSourceThumbnailMaxPixelSize: 2048] as CFDictionary
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let cg = CGImageSourceCreateThumbnailAtIndex(source, 0, options) else { return nil }
        let side = min(cg.width, cg.height)
        guard side > 0, let square = cg.cropping(to: CGRect(x: (cg.width - side) / 2, y: (cg.height - side) / 2,
                                                            width: side, height: side)) else { return nil }
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        let size = CGSize(width: 1024, height: 1024)
        return UIGraphicsImageRenderer(size: size, format: format)
            .image { _ in UIImage(cgImage: square).draw(in: CGRect(origin: .zero, size: size)) }
            .pngData()
    }
}

struct SettingsSheet: View {
    @ObservedObject private var icon = CustomIcon.shared
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @State private var photo: PhotosPickerItem?
    @State private var importing = false
    @State private var problem: String?
    @State private var saved = false

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "–"
    }

    var body: some View {
        NavigationStack {
            Form {
                iconSection
                if icon.image != nil { homeScreenSection }
                Section {
                    Link("Datenschutzerklärung", destination: Account.privacyURL)
                    LabeledContent("Version", value: version)
                }
            }
            .navigationTitle("Einstellungen")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Fertig") { dismiss() } } }
        }
        .onChange(of: photo) { _, item in
            guard let item else { return }
            Task {
                let data = try? await item.loadTransferable(type: Data.self)
                use(data)
                photo = nil
            }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.image]) { result in
            guard case .success(let url) = result else { return }
            let access = url.startAccessingSecurityScopedResource()
            defer { if access { url.stopAccessingSecurityScopedResource() } }
            use(try? Data(contentsOf: url))
        }
        .alert("App-Icon", isPresented: problemShown, presenting: problem) { _ in
            Button("OK") {}
        } message: { Text($0) }
    }

    private var iconSection: some View {
        Section {
            HStack(spacing: 16) {
                preview
                VStack(alignment: .leading, spacing: 2) {
                    Text(icon.image == nil ? "Standard-Icon" : "Eigenes Icon").font(.headline)
                    Text(icon.image == nil ? "Ein Bild wählen, es wird quadratisch zugeschnitten." : "Quadratisch zugeschnitten")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.vertical, 4)
            .accessibilityElement(children: .combine)

            PhotosPicker(selection: $photo, matching: .images) {
                Label("Foto auswählen", systemImage: "photo.on.rectangle")
            }
            Button("Aus Dateien auswählen", systemImage: "folder") { importing = true }
            if icon.image != nil {
                Button("Standard-Icon verwenden", systemImage: "arrow.uturn.backward", role: .destructive) {
                    icon.reset()
                    saved = false
                }
            }
        } header: {
            Text("App-Icon")
        } footer: {
            if icon.image == nil {
                Text("iOS lässt Apps ihr Symbol auf dem Home-Bildschirm nicht frei ändern. Mit der Kurzbefehle-App kommt dein Bild trotzdem dorthin.")
            }
        }
    }

    private var homeScreenSection: some View {
        Section {
            Button(saved ? "In Fotos gesichert" : "Bild in Fotos sichern",
                   systemImage: saved ? "checkmark.circle.fill" : "square.and.arrow.down") {
                Task { await saveToPhotos() }
            }
            .symbolEffect(.bounce, value: saved)
            .disabled(saved)
            Button("Kurzbefehle öffnen", systemImage: "arrow.up.forward.app") {
                if let url = URL(string: "shortcuts://create-shortcut") { openURL(url) }
            }
        } header: {
            Text("Auf dem Home-Bildschirm")
        } footer: {
            Text("Neuen Kurzbefehl mit der Aktion „App öffnen“ → ClipSound anlegen. Dann „Zum Home-Bildschirm“ wählen, auf das Symbol tippen und dein Bild aus den Fotos nehmen.")
        }
    }

    private var preview: some View {
        Group {
            if let image = icon.image {
                Image(uiImage: image).resizable()
            } else {
                Image("AppIconPreview").resizable()
            }
        }
        .frame(width: 60, height: 60)
        .clipShape(RoundedRectangle(cornerRadius: 13.5, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 13.5, style: .continuous).strokeBorder(.separator, lineWidth: 0.5))
        .accessibilityHidden(true)
    }

    private func use(_ data: Data?) {
        guard let data, icon.set(data) else {
            problem = "Das ist kein Bild, das ClipSound lesen kann."
            return
        }
        saved = false
    }

    private func saveToPhotos() async {
        switch await icon.saveToPhotos() {
        case .saved: saved = true
        case .denied: problem = "ClipSound darf nichts in Fotos sichern. Erlauben unter Einstellungen › ClipSound › Fotos."
        case .failed: problem = "Das Bild konnte nicht in Fotos gesichert werden."
        }
    }

    private var problemShown: Binding<Bool> {
        Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })
    }
}
