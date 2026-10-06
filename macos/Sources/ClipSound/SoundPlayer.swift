import AVFoundation
import Combine

/// Spielt Sounds über AVAudioEngine ab. Über 100 % wird mit einem EQ-Gain verstärkt (bis 500 % ≈ +14 dB).
final class SoundPlayer: ObservableObject {
    static let maxVolume = 5.0

    /// Fortschritt (0…1) pro laufendem Sound
    @Published private(set) var progress: [String: Double] = [:]

    @Published var volume: Double {
        didSet { applyVolume(); UserDefaults.standard.set(volume, forKey: "volume") }
    }
    @Published var overlap: Bool {
        didSet { UserDefaults.standard.set(overlap, forKey: "overlap") }
    }

    private let engine = AVAudioEngine()
    private let mixer = AVAudioMixerNode()               // sammelt alle Sounds, wandelt Formate um
    private let boost = AVAudioUnitEQ(numberOfBands: 0)  // Verstärkung über 0 dB

    private struct Voice { let node: AVAudioPlayerNode; let file: AVAudioFile; let soundID: String }
    private var voices: [UUID: Voice] = [:]
    private var timer: Timer?

    init() {
        let defaults = UserDefaults.standard
        volume = defaults.object(forKey: "volume") as? Double ?? 0.8
        overlap = defaults.bool(forKey: "overlap")

        engine.attach(mixer)
        engine.attach(boost)
        engine.connect(mixer, to: boost, format: nil)
        engine.connect(boost, to: engine.mainMixerNode, format: nil)
        applyVolume()
    }

    private func applyVolume() {
        let v = min(max(volume, 0), Self.maxVolume)
        if v <= 1 {
            mixer.outputVolume = Float(v)
            boost.globalGain = 0
        } else {
            mixer.outputVolume = 1
            boost.globalGain = Float(20 * log10(v)) // 5.0 → +13,98 dB
        }
    }

    /// `delay`: Start in so vielen Sekunden (Lobby: alle starten zur gleichen Zeit)
    func play(_ sound: Sound, delay: TimeInterval = 0) -> Bool {
        if !overlap { stopAll() }
        guard let file = try? AVAudioFile(forReading: sound.url) else { return false }
        if !engine.isRunning {
            do { try engine.start() } catch { return false }
        }

        let node = AVAudioPlayerNode()
        engine.attach(node)
        engine.connect(node, to: mixer, format: file.processingFormat)

        let id = UUID()
        voices[id] = Voice(node: node, file: file, soundID: sound.id)
        node.scheduleFile(file, at: nil, completionCallbackType: .dataPlayedBack) { [weak self] _ in
            DispatchQueue.main.async { self?.finish(id) }
        }
        if delay > 0.005 {
            node.play(at: AVAudioTime(hostTime: mach_absolute_time() + AVAudioTime.hostTime(forSeconds: delay)))
        } else {
            node.play()
        }
        progress[sound.id] = 0
        startTimer()
        return true
    }

    func stopAll() {
        for id in Array(voices.keys) { finish(id) }
    }

    func isPlaying(_ sound: Sound) -> Bool { progress[sound.id] != nil }

    private func finish(_ id: UUID) {
        guard let voice = voices.removeValue(forKey: id) else { return }
        voice.node.stop()
        engine.detach(voice.node)
        if !voices.values.contains(where: { $0.soundID == voice.soundID }) {
            progress[voice.soundID] = nil
        }
        if voices.isEmpty { timer?.invalidate(); timer = nil }
    }

    private func startTimer() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 1 / 30, repeats: true) { [weak self] _ in self?.tick() }
    }

    private func tick() {
        var next: [String: Double] = [:]
        for voice in voices.values {
            var p = 0.0
            if let t = voice.node.lastRenderTime, let pt = voice.node.playerTime(forNodeTime: t), voice.file.length > 0 {
                p = min(1, max(0, Double(pt.sampleTime) / Double(voice.file.length)))
            }
            next[voice.soundID] = max(next[voice.soundID] ?? 0, p)
        }
        progress = next
    }
}
