import SwiftUI

/// Dieselbe Farbzuordnung wie die Kacheln im Hauptfenster – damit ein Sound im
/// Mischpult die gleiche Farbe trägt wie seine Kachel.
func clipTint(for id: String) -> Color {
    let colors: [Color] = [.blue, .purple, .pink, .orange, .green, .teal, .indigo, .red]
    var h: UInt32 = 0
    for unit in id.utf16 { h = h &* 31 &+ UInt32(unit) }
    return colors[Int(h % UInt32(colors.count))]
}

/// Mischpult für iPhone/iPad: EQ, ein Regler pro laufendem Sound (mit Plattenteller zum
/// Scratchen), Master-Lautstärke bis 500 % und Ein-/Ausblenden – alles, was die Mac-App kann.
/// Die Audio-Engine (`SoundPlayer`) ist dieselbe wie am Mac; hier kommt nur die Bedienung dazu.
struct MixerView: View {
    @ObservedObject var player: SoundPlayer
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                channelsSection
                masterSection
                eqSection
                fadeSection
            }
            .navigationTitle("Mischpult")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
        }
    }

    // MARK: Kanäle (laufende Sounds)

    @ViewBuilder private var channelsSection: some View {
        Section {
            if player.channels.isEmpty {
                Text("Spiel einen Sound ab – er erscheint hier mit eigenem Regler bis 200 %, Plattenteller und Ausblenden.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(player.channels) { channel in
                    ChannelRow(
                        channel: channel,
                        level: Binding(get: { channel.level },
                                       set: { player.setLevel($0, for: channel.soundID) }),
                        onFade: { player.fadeOut(channel.id) },
                        onStop: { player.stop(channel.id) },
                        onScratchBegin: { player.beginScratch(channel.id) },
                        onScratch: { player.scratch(channel.id, by: $0) },
                        onScratchEnd: { player.endScratch(channel.id) })
                }
            }
        } header: {
            Text("Kanäle")
        }
    }

    // MARK: Master

    private var masterSection: some View {
        Section("Master") {
            HStack(spacing: 12) {
                Image(systemName: "speaker.wave.2.fill", variableValue: min(player.volume, 1))
                    .foregroundStyle(.secondary)
                    .frame(width: 24)
                Slider(value: $player.volume, in: 0...SoundPlayer.maxVolume)
                PercentLabel(value: player.volume)
                    .onTapGesture { player.volume = 1 }
            }
            Button {
                player.fadeOutAll()
            } label: {
                Label("Alles ausblenden", systemImage: "speaker.wave.1")
            }
            .disabled(player.channels.isEmpty)
        }
    }

    // MARK: EQ

    private var eqSection: some View {
        Section {
            EQRow(title: "Bass", value: $player.bass)
            EQRow(title: "Mitten", value: $player.mid)
            EQRow(title: "Höhen", value: $player.treble)
        } header: {
            Text("EQ")
        } footer: {
            Text("Hebt oder senkt Bässe, Mitten und Höhen für alles zusammen. Tippen auf den Wert stellt auf 0 dB zurück.")
        }
    }

    // MARK: Ein- und Ausblenden

    private var fadeSection: some View {
        Section {
            Toggle("Neue Sounds einblenden", isOn: $player.fadeIn)
            Picker("Fade-Dauer", selection: $player.fadeSeconds) {
                Text("0,5 s").tag(0.5)
                Text("1 s").tag(1.0)
                Text("2 s").tag(2.0)
                Text("4 s").tag(4.0)
                Text("8 s").tag(8.0)
            }
        } header: {
            Text("Blenden")
        } footer: {
            Text("Sounds starten leise und werden lauter. Die Dauer gilt auch fürs Ausblenden.")
        }
    }
}

// MARK: - Bausteine

private struct PercentLabel: View {
    let value: Double
    var body: some View {
        Text(value, format: .percent.precision(.fractionLength(0)))
            .font(.callout.monospacedDigit())
            .foregroundStyle(value > 1 ? Color.orange : Color.secondary)
            .frame(width: 52, alignment: .trailing)
    }
}

/// EQ-Regler mit dB-Anzeige. Tippen auf den Wert stellt auf 0 dB.
private struct EQRow: View {
    let title: String
    @Binding var value: Double

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(title)
                Spacer()
                Text(value.rounded() == 0 ? "0 dB" : String(format: "%+.0f dB", value))
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .onTapGesture { value = 0 }
            }
            Slider(value: $value, in: SoundPlayer.eqRange, step: 1)
        }
    }
}

/// Ein laufender Sound: Plattenteller, Lautstärke-Regler, Fortschritt, Ausblenden/Stopp.
private struct ChannelRow: View {
    let channel: SoundPlayer.Channel
    @Binding var level: Double
    let onFade: () -> Void
    let onStop: () -> Void
    let onScratchBegin: () -> Void
    let onScratch: (Double) -> Void
    let onScratchEnd: () -> Void

    var body: some View {
        let tint = clipTint(for: channel.soundID)
        HStack(alignment: .top, spacing: 14) {
            Turntable(position: channel.position, tint: tint,
                      onBegin: onScratchBegin, onTurn: onScratch, onEnd: onScratchEnd)
                .frame(width: 60, height: 60)

            VStack(alignment: .leading, spacing: 6) {
                Text(channel.title)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)

                HStack(spacing: 10) {
                    Slider(value: $level, in: 0...SoundPlayer.maxChannelLevel)
                        .tint(tint)
                    PercentLabel(value: channel.level)
                }

                ProgressView(value: channel.progress)
                    .progressViewStyle(.linear)
                    .tint(tint)

                HStack(spacing: 18) {
                    Button(action: onFade) {
                        Label("Ausblenden", systemImage: "speaker.wave.1")
                    }
                    .disabled(channel.fadingOut)
                    Button(role: .destructive, action: onStop) {
                        Label("Stopp", systemImage: "stop.fill")
                    }
                    Spacer()
                }
                .labelStyle(.iconOnly)
                .font(.system(size: 17))
                .buttonStyle(.borderless)
            }
        }
        .padding(.vertical, 4)
        .opacity(channel.fadingOut ? 0.6 : 1)
    }
}

/// Plattenteller: dreht sich mit dem Sound, zum Vor- und Zurückspulen (Scratchen) mit dem Finger drehen.
private struct Turntable: View {
    let position: Double
    let tint: Color
    let onBegin: () -> Void
    let onTurn: (Double) -> Void
    let onEnd: () -> Void

    @State private var lastAngle: Double?

    var body: some View {
        GeometryReader { geo in
            let size = min(geo.size.width, geo.size.height)
            let center = CGPoint(x: geo.size.width / 2, y: geo.size.height / 2)
            record(size: size)
                .position(center)
                .gesture(scratchGesture(center: center))
        }
        .accessibilityLabel("Plattenteller – drehen zum Spulen")
    }

    private func record(size: CGFloat) -> some View {
        let disc = Color(white: 0.12)
        let degrees = position / SoundPlayer.secondsPerTurn * 360
        return ZStack {
            Circle().fill(disc)
            ForEach(1..<5) { (i: Int) in
                Circle()
                    .strokeBorder(Color.white.opacity(0.07), lineWidth: 1)
                    .padding(size * 0.05 * CGFloat(i))
            }
            Circle().fill(tint).frame(width: size * 0.36, height: size * 0.36)
            Circle().fill(disc).frame(width: size * 0.06, height: size * 0.06)
            Capsule()
                .fill(Color.white.opacity(0.85))
                .frame(width: 2, height: size * 0.3)
                .offset(y: -size * 0.3)
        }
        .frame(width: size, height: size)
        .rotationEffect(.degrees(degrees))
        .contentShape(Circle())
    }

    private func scratchGesture(center: CGPoint) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { drag in
                let angle = Double(atan2(drag.location.y - center.y, drag.location.x - center.x))
                if let lastAngle {
                    var delta = angle - lastAngle
                    if delta > .pi { delta -= 2 * .pi } else if delta < -.pi { delta += 2 * .pi }
                    onTurn(delta / (2 * .pi) * SoundPlayer.secondsPerTurn)
                } else {
                    onBegin()
                }
                lastAngle = angle
            }
            .onEnded { _ in
                lastAngle = nil
                onEnd()
            }
    }
}
