import SwiftUI

/// Mischpult in der unteren Fensterhälfte: EQ, ein Kanal pro laufendem Sound, Master
struct MixerView: View {
    @ObservedObject var player: SoundPlayer

    var body: some View {
        HStack(spacing: 0) {
            eqSection
            Divider()
            channelSection
            Divider()
            masterSection
        }
        .background(.bar)
    }

    // MARK: EQ

    private var eqSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle("EQ")
            HStack(spacing: 4) {
                EQStrip(title: "Bass", value: $player.bass)
                EQStrip(title: "Mitten", value: $player.mid)
                EQStrip(title: "Höhen", value: $player.treble)
            }
        }
        .padding(12)
    }

    // MARK: Kanäle

    private var channelSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle("Kanäle")
            if player.channels.isEmpty {
                Text("Spiel einen Sound ab – er erscheint hier mit eigenem Regler bis 200 %.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .multilineTextAlignment(.center)
            } else {
                ScrollView(.horizontal) {
                    HStack(spacing: 4) {
                        ForEach(player.channels) { channel in
                            ChannelStrip(channel: channel,
                                         level: Binding(get: { channel.level },
                                                        set: { player.setLevel($0, for: channel.soundID) }),
                                         onFade: { player.fadeOut(channel.id) },
                                         onStop: { player.stop(channel.id) },
                                         onScratchBegin: { player.beginScratch(channel.id) },
                                         onScratch: { player.scratch(channel.id, by: $0) },
                                         onScratchEnd: { player.endScratch(channel.id) })
                        }
                    }
                    .frame(maxHeight: .infinity)
                }
                .scrollIndicators(.automatic)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity)
    }

    // MARK: Master

    private var masterSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle("Master")
            HStack(alignment: .top, spacing: 12) {
                VStack(spacing: 6) {
                    Fader(value: $player.volume, range: 0...SoundPlayer.maxVolume, mark: 1, reset: 1)
                    PercentLabel(value: player.volume)
                }
                .frame(width: 44)

                VStack(alignment: .leading, spacing: 10) {
                    Button {
                        player.fadeOutAll()
                    } label: {
                        Label("Alles ausblenden", systemImage: "speaker.wave.1")
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .disabled(player.channels.isEmpty)
                    .help("Alle laufenden Sounds weich ausblenden")

                    Toggle("Neue Sounds einblenden", isOn: $player.fadeIn)
                        .toggleStyle(.checkbox)
                        .help("Sounds starten leise und werden lauter")

                    VStack(alignment: .leading, spacing: 4) {
                        Text("Fade-Dauer").font(.caption).foregroundStyle(.secondary)
                        Picker("Fade-Dauer", selection: $player.fadeSeconds) {
                            Text("0,5 s").tag(0.5)
                            Text("1 s").tag(1.0)
                            Text("2 s").tag(2.0)
                            Text("4 s").tag(4.0)
                            Text("8 s").tag(8.0)
                        }
                        .labelsHidden()
                        .fixedSize()
                    }
                }
                .controlSize(.small)
                .frame(width: 160)
            }
        }
        .padding(12)
    }
}

private struct SectionTitle: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .textCase(.uppercase)
    }
}

private struct PercentLabel: View {
    let value: Double
    var body: some View {
        Text(value, format: .percent.precision(.fractionLength(0)))
            .font(.caption.monospacedDigit())
            .foregroundStyle(value > 1 ? Color.orange : Color.secondary)
    }
}

private struct EQStrip: View {
    let title: String
    @Binding var value: Double

    var body: some View {
        VStack(spacing: 6) {
            Fader(value: $value, range: SoundPlayer.eqRange, mark: 0, reset: 0)
            Text(value.rounded() == 0 ? "0 dB" : String(format: "%+.0f dB", value))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
            Text(title).font(.caption)
        }
        .frame(width: 52)
    }
}

private struct ChannelStrip: View {
    let channel: SoundPlayer.Channel
    @Binding var level: Double
    let onFade: () -> Void
    let onStop: () -> Void
    let onScratchBegin: () -> Void
    let onScratch: (Double) -> Void
    let onScratchEnd: () -> Void

    var body: some View {
        let tint = PadView.tint(for: channel.soundID)
        VStack(spacing: 6) {
            Turntable(position: channel.position, tint: tint,
                      onBegin: onScratchBegin, onTurn: onScratch, onEnd: onScratchEnd)
                .frame(width: 68, height: 68)
            Fader(value: $level, range: 0...SoundPlayer.maxChannelLevel, mark: 1, reset: 1, tint: tint)
            PercentLabel(value: channel.level)
            ProgressView(value: channel.progress)
                .progressViewStyle(.linear)
                .tint(tint)
                .controlSize(.mini)
            Text(channel.title)
                .font(.caption)
                .lineLimit(2, reservesSpace: true)
                .multilineTextAlignment(.center)
                .help(channel.title)
            HStack(spacing: 2) {
                Button("Ausblenden", systemImage: "speaker.wave.1", action: onFade)
                    .disabled(channel.fadingOut)
                    .help("Weich ausblenden")
                Button("Stopp", systemImage: "stop.fill", action: onStop)
                    .help("Sofort stoppen")
            }
            .labelStyle(.iconOnly)
            .buttonStyle(.borderless)
            .font(.system(size: 14))
        }
        .padding(8)
        .frame(width: 84)
        .background(.quinary, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .opacity(channel.fadingOut ? 0.6 : 1)
    }
}

/// Plattenteller: dreht sich mit dem Sound, mit der Maus vor- und zurückdrehen (Scratchen)
private struct Turntable: View {
    let position: Double
    let tint: Color
    let onBegin: () -> Void
    let onTurn: (Double) -> Void
    let onEnd: () -> Void

    @State private var lastAngle: Double?

    var body: some View {
        GeometryReader { geo in
            let size: CGFloat = min(geo.size.width, geo.size.height)
            let center = CGPoint(x: geo.size.width / 2, y: geo.size.height / 2)
            record(size: size)
                .position(center)
                .gesture(scratchGesture(center: center))
        }
        .help("Drehen zum Vor- und Zurückspulen")
        .accessibilityLabel("Plattenteller")
    }

    private func record(size: CGFloat) -> some View {
        let disc = Color(white: 0.12)
        let degrees: Double = position / SoundPlayer.secondsPerTurn * 360
        return ZStack {
            Circle().fill(disc)
            ForEach(1..<5) { (i: Int) in
                Circle()
                    .strokeBorder(Color.white.opacity(0.07), lineWidth: 1)
                    .padding(size * 0.05 * CGFloat(i))
            }
            Circle().fill(tint).frame(width: size * 0.36, height: size * 0.36)
            Circle().fill(disc).frame(width: size * 0.06, height: size * 0.06)
            // Markierung, damit man das Drehen sieht
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

/// Senkrechter Schieberegler wie am Mischpult. Doppelklick setzt auf `reset` zurück, nahe `mark` rastet er ein.
struct Fader: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    var mark: Double?
    var reset: Double
    var tint: Color = .accentColor

    @State private var lastClick = Date.distantPast

    private let thumbHeight: CGFloat = 12

    var body: some View {
        GeometryReader { geo in
            let usable = max(1, geo.size.height - thumbHeight)
            let y = { (v: Double) in thumbHeight / 2 + usable * (1 - fraction(v)) }
            let base = mark ?? range.lowerBound

            ZStack(alignment: .top) {
                Capsule()
                    .fill(.quaternary)
                    .frame(width: 4)
                    .frame(maxHeight: .infinity)
                Rectangle()
                    .fill(tint)
                    .frame(width: 4, height: abs(y(value) - y(base)))
                    .offset(y: min(y(value), y(base)))
                if let mark {
                    Rectangle()
                        .fill(.secondary)
                        .frame(width: 18, height: 1)
                        .offset(y: y(mark))
                }
                RoundedRectangle(cornerRadius: 3, style: .continuous)
                    .fill(Color(nsColor: .controlColor))
                    .overlay { RoundedRectangle(cornerRadius: 3, style: .continuous).strokeBorder(.separator) }
                    .overlay { Rectangle().fill(.secondary).frame(height: 1).padding(.horizontal, 5) }
                    .shadow(color: .black.opacity(0.2), radius: 1, y: 1)
                    .frame(width: 28, height: thumbHeight)
                    .offset(y: y(value) - thumbHeight / 2)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { drag in set(fromY: drag.location.y, usable: usable) }
                .onEnded { drag in
                    // Doppelklick: zurück auf den Normalwert
                    let now = Date()
                    if abs(drag.translation.height) < 2 {
                        if now.timeIntervalSince(lastClick) < NSEvent.doubleClickInterval { value = reset; lastClick = .distantPast; return }
                        lastClick = now
                    }
                })
        }
        .frame(minHeight: 60)
        .accessibilityRepresentation {
            Slider(value: $value, in: range)
        }
    }

    private func fraction(_ v: Double) -> Double {
        (min(max(v, range.lowerBound), range.upperBound) - range.lowerBound) / (range.upperBound - range.lowerBound)
    }

    private func set(fromY y: CGFloat, usable: CGFloat) {
        let f = 1 - min(max((y - thumbHeight / 2) / usable, 0), 1)
        var v = range.lowerBound + Double(f) * (range.upperBound - range.lowerBound)
        if let mark, abs(v - mark) < (range.upperBound - range.lowerBound) * 0.025 { v = mark }
        value = v
    }
}
