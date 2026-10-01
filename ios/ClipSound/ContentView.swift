import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @ObservedObject var library: SoundLibrary
    @ObservedObject var player: SoundPlayer

    @State private var query = ""
    @State private var importing = false
    @State private var pendingDelete: Sound?
    @State private var problem: String?
    @State private var tapCount = 0

    private var visible: [Sound] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? library.sounds : library.sounds.filter { $0.id.lowercased().contains(q) || $0.title.lowercased().contains(q) }
    }

    var body: some View {
        NavigationStack {
            Group {
                if library.sounds.isEmpty {
                    ContentUnavailableView {
                        Label("Keine Sounds", systemImage: "waveform")
                    } description: {
                        Text("Importiere Sounds aus der Dateien-App – einzeln oder als ganzen Ordner.")
                    } actions: {
                        Button("Sounds importieren") { importing = true }
                            .buttonStyle(.borderedProminent)
                    }
                } else if visible.isEmpty {
                    ContentUnavailableView.search(text: query)
                } else {
                    grid
                }
            }
            .navigationTitle("ClipSound")
            .searchable(text: $query, prompt: "Suchen")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Alles stoppen", systemImage: "stop.fill", action: player.stopAll)
                        .disabled(player.progress.isEmpty)
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Importieren", systemImage: "plus") { importing = true }
                }
            }
            .safeAreaInset(edge: .bottom) { controls }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.audio, .folder], allowsMultipleSelection: true) { result in
            switch result {
            case .success(let urls):
                let r = library.importItems(urls)
                if !r.rejected.isEmpty {
                    problem = "Keine Sounds, übersprungen:\n" + r.rejected.prefix(5).joined(separator: "\n")
                }
            case .failure(let error):
                problem = error.localizedDescription
            }
        }
        .confirmationDialog("„\(pendingDelete?.title ?? "")“ löschen?", isPresented: deleteShown, titleVisibility: .visible, presenting: pendingDelete) { sound in
            Button("Löschen", role: .destructive) { library.delete(sound) }
        }
        .alert("ClipSound", isPresented: problemShown, presenting: problem) { _ in
            Button("OK") {}
        } message: { Text($0) }
        .sensoryFeedback(.impact(weight: .light), trigger: tapCount)
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 10)], spacing: 10) {
                ForEach(visible) { sound in
                    Tile(sound: sound, progress: player.progress[sound.id]) {
                        tapCount += 1
                        if !player.play(sound) { problem = "„\(sound.title)“ konnte nicht abgespielt werden." }
                    }
                    .contextMenu {
                        Button("Abspielen", systemImage: "play") { _ = player.play(sound) }
                        Button("Löschen", systemImage: "trash", role: .destructive) { pendingDelete = sound }
                    }
                }
            }
            .padding(16)
        }
        .background(Color(.systemGroupedBackground))
    }

    /// Lautstärke und Überlappen unten
    private var controls: some View {
        HStack(spacing: 12) {
            Image(systemName: "speaker.wave.2.fill", variableValue: min(player.volume, 1))
                .foregroundStyle(.secondary)
                .frame(width: 24)
            Slider(value: $player.volume, in: 0...SoundPlayer.maxVolume)
            Text(player.volume, format: .percent.precision(.fractionLength(0)))
                .monospacedDigit()
                .foregroundStyle(player.volume > 1 ? Color.orange : Color.secondary)
                .frame(width: 48, alignment: .trailing)
                .onTapGesture { player.volume = 1 }
            Toggle(isOn: $player.overlap) {
                Image(systemName: "square.stack.3d.up")
            }
            .toggleStyle(.button)
            .accessibilityLabel("Überlappen")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .background(.bar)
    }

    private var deleteShown: Binding<Bool> {
        Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } })
    }

    private var problemShown: Binding<Bool> {
        Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })
    }
}

/// Eine Sound-Kachel – gleiche Gestaltung wie in der Mac-App
struct Tile: View {
    let sound: Sound
    let progress: Double?
    let action: () -> Void

    private var tint: Color {
        let colors: [Color] = [.blue, .purple, .pink, .orange, .green, .teal, .indigo, .red]
        var h: UInt32 = 0
        for unit in sound.id.utf16 { h = h &* 31 &+ UInt32(unit) }
        return colors[Int(h % UInt32(colors.count))]
    }

    var body: some View {
        let playing = progress != nil
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)

        Button(action: action) {
            VStack(alignment: .leading, spacing: 0) {
                Image(systemName: playing ? "speaker.wave.2.fill" : "waveform")
                    .symbolEffect(.variableColor.iterative, isActive: playing)
                    .font(.system(size: 17, weight: .medium))
                    .foregroundStyle(tint)
                    .frame(height: 22, alignment: .leading)
                Spacer(minLength: 6)
                Text(sound.title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(12)
            .frame(height: 92)
            .background {
                shape.fill(playing ? AnyShapeStyle(tint.opacity(0.16)) : AnyShapeStyle(Color(.secondarySystemGroupedBackground)))
            }
            .overlay(alignment: .bottom) {
                if let progress {
                    GeometryReader { geo in
                        Capsule().fill(tint).frame(width: max(4, geo.size.width * progress), height: 3)
                    }
                    .frame(height: 3)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 5)
                }
            }
            .overlay { shape.strokeBorder(playing ? tint.opacity(0.7) : Color.primary.opacity(0.06), lineWidth: 1) }
            .contentShape(shape)
        }
        .buttonStyle(.plain)
    }
}
