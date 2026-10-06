import AVFoundation
import Combine

/// Spielt Sounds über AVAudioEngine ab. Über 100 % wird mit einem EQ-Gain verstärkt (bis 500 % ≈ +14 dB).
///
/// Signalweg: Sound → Kanal (eigener Gain, 0–200 %, Fades) → Mixer → Master-EQ (Bass/Mitten/Höhen + Verstärkung) → Ausgang
final class SoundPlayer: ObservableObject {
    static let maxVolume = 5.0
    /// Höchste Lautstärke eines einzelnen Kanals (200 % ≈ +6 dB, echte Verstärkung)
    static let maxChannelLevel = 2.0
    /// Bereich der EQ-Regler in dB
    static let eqRange = -12.0...12.0

    /// Ein laufender Sound im Mischpult
    struct Channel: Identifiable, Equatable {
        let id: UUID
        let soundID: String
        let title: String
        var level: Double
        var progress: Double
        var fadingOut: Bool
    }

    /// Fortschritt (0…1) pro laufendem Sound
    @Published private(set) var progress: [String: Double] = [:]
    /// Laufende Sounds in Startreihenfolge
    @Published private(set) var channels: [Channel] = []

    @Published var volume: Double {
        didSet { applyVolume(); UserDefaults.standard.set(volume, forKey: "volume") }
    }
    @Published var overlap: Bool {
        didSet { UserDefaults.standard.set(overlap, forKey: "overlap") }
    }
    @Published var bass: Double { didSet { applyEQ(); UserDefaults.standard.set(bass, forKey: "eqBass") } }
    @Published var mid: Double { didSet { applyEQ(); UserDefaults.standard.set(mid, forKey: "eqMid") } }
    @Published var treble: Double { didSet { applyEQ(); UserDefaults.standard.set(treble, forKey: "eqTreble") } }
    /// Neue Sounds weich einblenden
    @Published var fadeIn: Bool { didSet { UserDefaults.standard.set(fadeIn, forKey: "fadeIn") } }
    /// Dauer von Ein- und Ausblenden in Sekunden
    @Published var fadeSeconds: Double { didSet { UserDefaults.standard.set(fadeSeconds, forKey: "fadeSeconds") } }

    private let engine = AVAudioEngine()
    private let mixer = AVAudioMixerNode()               // sammelt alle Sounds, wandelt Formate um
    private let master = AVAudioUnitEQ(numberOfBands: 3) // Bass/Mitten/Höhen + Verstärkung über 0 dB

    private struct Fade { let from: Double; let to: Double; let start: TimeInterval; let duration: TimeInterval; let stopAtEnd: Bool }
    private final class Voice {
        let node: AVAudioPlayerNode
        let gain: AVAudioUnitEQ
        let file: AVAudioFile
        let soundID: String
        let title: String
        let started = Date()
        var level = 1.0
        var fadeValue = 1.0
        var fade: Fade?
        init(node: AVAudioPlayerNode, gain: AVAudioUnitEQ, file: AVAudioFile, soundID: String, title: String) {
            self.node = node; self.gain = gain; self.file = file; self.soundID = soundID; self.title = title
        }
    }
    private var voices: [UUID: Voice] = [:]
    private var timer: Timer?

    init() {
        let defaults = UserDefaults.standard
        volume = defaults.object(forKey: "volume") as? Double ?? 0.8
        overlap = defaults.bool(forKey: "overlap")
        bass = defaults.double(forKey: "eqBass")
        mid = defaults.double(forKey: "eqMid")
        treble = defaults.double(forKey: "eqTreble")
        fadeIn = defaults.bool(forKey: "fadeIn")
        fadeSeconds = defaults.object(forKey: "fadeSeconds") as? Double ?? 2

        let bands = master.bands
        bands[0].filterType = .lowShelf;  bands[0].frequency = 150
        bands[1].filterType = .parametric; bands[1].frequency = 1000; bands[1].bandwidth = 1.5
        bands[2].filterType = .highShelf; bands[2].frequency = 6000
        for band in bands { band.bypass = false; band.gain = 0 }

        engine.attach(mixer)
        engine.attach(master)
        engine.connect(mixer, to: master, format: nil)
        engine.connect(master, to: engine.mainMixerNode, format: nil)
        applyVolume()
        applyEQ()
    }

    private func applyVolume() {
        let v = min(max(volume, 0), Self.maxVolume)
        if v <= 1 {
            mixer.outputVolume = Float(v)
            master.globalGain = 0
        } else {
            mixer.outputVolume = 1
            master.globalGain = Float(20 * log10(v)) // 5.0 → +13,98 dB
        }
    }

    private func applyEQ() {
        let r = Self.eqRange
        master.bands[0].gain = Float(min(max(bass, r.lowerBound), r.upperBound))
        master.bands[1].gain = Float(min(max(mid, r.lowerBound), r.upperBound))
        master.bands[2].gain = Float(min(max(treble, r.lowerBound), r.upperBound))
    }

    /// Lautstärke eines Kanals: Regler × Fade, als dB auf den Kanal-EQ (0 → stumm)
    private func applyGain(_ voice: Voice) {
        let linear = voice.level * voice.fadeValue
        voice.gain.globalGain = linear <= 0.0001 ? -96 : Float(max(-96, 20 * log10(linear)))
    }

    /// `delay`: Start in so vielen Sekunden (Lobby: alle starten zur gleichen Zeit)
    func play(_ sound: Sound, delay: TimeInterval = 0) -> Bool {
        if !overlap { stopAll() }
        guard let file = try? AVAudioFile(forReading: sound.url) else { return false }

        let node = AVAudioPlayerNode()
        let gain = AVAudioUnitEQ(numberOfBands: 0)
        engine.attach(node)
        engine.attach(gain)
        engine.connect(node, to: gain, format: file.processingFormat)
        engine.connect(gain, to: mixer, format: file.processingFormat)
        if !engine.isRunning {
            do { try engine.start() } catch { engine.detach(node); engine.detach(gain); return false }
        }

        let id = UUID()
        let voice = Voice(node: node, gain: gain, file: file, soundID: sound.id, title: sound.title)
        if fadeIn && fadeSeconds > 0 {
            voice.fadeValue = 0
            voice.fade = Fade(from: 0, to: 1, start: Date().timeIntervalSinceReferenceDate + max(0, delay),
                              duration: fadeSeconds, stopAtEnd: false)
        }
        applyGain(voice)
        voices[id] = voice
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
        tick()
        return true
    }

    func stopAll() {
        for id in Array(voices.keys) { finish(id) }
    }

    func stop(_ id: UUID) { finish(id) }

    func isPlaying(_ sound: Sound) -> Bool { progress[sound.id] != nil }

    /// Regler eines Kanals (0…200 %)
    func setLevel(_ level: Double, for id: UUID) {
        guard let voice = voices[id] else { return }
        voice.level = min(max(level, 0), Self.maxChannelLevel)
        applyGain(voice)
        if let i = channels.firstIndex(where: { $0.id == id }) { channels[i].level = voice.level }
    }

    /// Blendet einen Kanal aus und stoppt ihn danach
    func fadeOut(_ id: UUID) {
        guard let voice = voices[id] else { return }
        guard fadeSeconds > 0 else { finish(id); return }
        voice.fade = Fade(from: voice.fadeValue, to: 0, start: Date().timeIntervalSinceReferenceDate,
                          duration: fadeSeconds * voice.fadeValue, stopAtEnd: true)
        tick()
    }

    func fadeOutAll() {
        for id in Array(voices.keys) { fadeOut(id) }
    }

    private func finish(_ id: UUID) {
        guard let voice = voices.removeValue(forKey: id) else { return }
        voice.node.stop()
        engine.detach(voice.node)
        engine.detach(voice.gain)
        if !voices.values.contains(where: { $0.soundID == voice.soundID }) {
            progress[voice.soundID] = nil
        }
        channels.removeAll { $0.id == id }
        if voices.isEmpty { timer?.invalidate(); timer = nil }
    }

    private func startTimer() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 1 / 30, repeats: true) { [weak self] _ in self?.tick() }
    }

    private func tick() {
        let now = Date().timeIntervalSinceReferenceDate
        var finished: [UUID] = []
        for (id, voice) in voices {
            guard let fade = voice.fade else { continue }
            let t = fade.duration > 0 ? min(1, max(0, (now - fade.start) / fade.duration)) : 1
            voice.fadeValue = fade.from + (fade.to - fade.from) * t
            applyGain(voice)
            if t >= 1 {
                voice.fade = nil
                if fade.stopAtEnd { finished.append(id) }
            }
        }
        for id in finished { finish(id) }

        var next: [String: Double] = [:]
        var list: [Channel] = []
        for (id, voice) in voices.sorted(by: { $0.value.started < $1.value.started }) {
            var p = 0.0
            if let t = voice.node.lastRenderTime, let pt = voice.node.playerTime(forNodeTime: t), voice.file.length > 0 {
                p = min(1, max(0, Double(pt.sampleTime) / Double(voice.file.length)))
            }
            next[voice.soundID] = max(next[voice.soundID] ?? 0, p)
            list.append(Channel(id: id, soundID: voice.soundID, title: voice.title, level: voice.level,
                                progress: p, fadingOut: voice.fade?.stopAtEnd == true))
        }
        progress = next
        if list != channels { channels = list }
    }
}
