import AVFoundation
import Combine
import os

/// Änderung am Mischpult, die in einer Lobby an alle geht (Sounds über ihre Datei-ID)
enum MixChange {
    case settings(bass: Double, mid: Double, treble: Double, fadeIn: Bool, fadeSeconds: Double)
    case level(soundID: String, value: Double)
    case fadeOut(soundID: String)
    /// Plattenteller: `position` in Sekunden ab Anfang, `holding` = Hand liegt auf dem Teller
    case scratch(soundID: String, position: Double, holding: Bool)
}

/// Spielt Sounds über AVAudioEngine ab. Über 100 % wird mit einem EQ-Gain verstärkt (bis 500 % ≈ +14 dB).
///
/// Signalweg: Sound (Plattenteller: läuft vor/zurück) → Kanal (Gain 0–200 %, Fades) → Mixer
/// → Master-EQ (Bass/Mitten/Höhen + Verstärkung) → Ausgang
final class SoundPlayer: ObservableObject {
    static let maxVolume = 5.0
    /// Höchste Lautstärke eines Sounds (200 % ≈ +6 dB, echte Verstärkung)
    static let maxChannelLevel = 2.0
    /// Bereich der EQ-Regler in dB
    static let eqRange = -12.0...12.0
    /// Eine Umdrehung des Plattentellers in Sekunden (33⅓ U/min)
    static let secondsPerTurn = 1.8

    /// Ein laufender Sound im Mischpult
    struct Channel: Identifiable, Equatable {
        let id: UUID
        let soundID: String
        let title: String
        var level: Double
        var progress: Double
        /// Abspielposition in Sekunden (dreht den Teller)
        var position: Double
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
    @Published var bass: Double { didSet { applyEQ(); settingsChanged("eqBass", bass) } }
    @Published var mid: Double { didSet { applyEQ(); settingsChanged("eqMid", mid) } }
    @Published var treble: Double { didSet { applyEQ(); settingsChanged("eqTreble", treble) } }
    /// Neue Sounds weich einblenden
    @Published var fadeIn: Bool { didSet { settingsChanged("fadeIn", fadeIn) } }
    /// Dauer von Ein- und Ausblenden in Sekunden
    @Published var fadeSeconds: Double { didSet { settingsChanged("fadeSeconds", fadeSeconds) } }

    /// Eigene Änderungen am Mischpult (für die Lobby). Nicht für Änderungen, die von dort kommen.
    var onChange: ((MixChange) -> Void)?
    private var applyingRemote = false

    /// Gemerkte Lautstärke pro Sound (Datei-ID → 0…2)
    private var levels: [String: Double] {
        didSet { UserDefaults.standard.set(levels, forKey: "soundLevels") }
    }

    private let engine = AVAudioEngine()
    private let mixer = AVAudioMixerNode()               // sammelt alle Sounds, wandelt Formate um
    private let master = AVAudioUnitEQ(numberOfBands: 3) // Bass/Mitten/Höhen + Verstärkung über 0 dB

    private struct Fade { let from: Double; let to: Double; let start: TimeInterval; let duration: TimeInterval; let stopAtEnd: Bool }
    private final class Voice {
        let deck: Deck
        let node: AVAudioSourceNode
        let gain: AVAudioUnitEQ
        let soundID: String
        let title: String
        let started = Date()
        var fadeValue = 1.0
        var fade: Fade?
        init(deck: Deck, node: AVAudioSourceNode, gain: AVAudioUnitEQ, soundID: String, title: String) {
            self.deck = deck; self.node = node; self.gain = gain; self.soundID = soundID; self.title = title
        }
    }
    private var voices: [UUID: Voice] = [:]
    private var timer: Timer?
    /// Zuletzt dekodierte Dateien, damit oft gedrückte Sounds sofort starten
    private var decoded: [URL: AVAudioPCMBuffer] = [:]
    private var decodedOrder: [URL] = []

    init() {
        let defaults = UserDefaults.standard
        volume = defaults.object(forKey: "volume") as? Double ?? 0.8
        overlap = defaults.bool(forKey: "overlap")
        bass = defaults.double(forKey: "eqBass")
        mid = defaults.double(forKey: "eqMid")
        treble = defaults.double(forKey: "eqTreble")
        fadeIn = defaults.bool(forKey: "fadeIn")
        fadeSeconds = defaults.object(forKey: "fadeSeconds") as? Double ?? 2
        levels = defaults.dictionary(forKey: "soundLevels") as? [String: Double] ?? [:]

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

    private func settingsChanged(_ key: String, _ value: Any) {
        UserDefaults.standard.set(value, forKey: key)
        guard !applyingRemote else { return }
        onChange?(.settings(bass: bass, mid: mid, treble: treble, fadeIn: fadeIn, fadeSeconds: fadeSeconds))
    }

    /// Lautstärke eines Kanals: Regler × Fade, als dB auf den Kanal-EQ (0 → stumm)
    private func applyGain(_ voice: Voice) {
        let linear = level(of: voice.soundID) * voice.fadeValue
        voice.gain.globalGain = linear <= 0.0001 ? -96 : Float(max(-96, 20 * log10(linear)))
    }

    func level(of soundID: String) -> Double { levels[soundID] ?? 1 }

    // MARK: Abspielen

    /// `delay`: Start in so vielen Sekunden (Lobby: alle starten zur gleichen Zeit)
    func play(_ sound: Sound, delay: TimeInterval = 0) -> Bool {
        if !overlap { stopAll() }
        guard let buffer = buffer(for: sound.url) else { return false }
        let start = delay > 0.005 ? mach_absolute_time() + AVAudioTime.hostTime(forSeconds: delay) : 0
        let deck = Deck(buffer: buffer, startHostTime: start)
        let node = AVAudioSourceNode(format: buffer.format) { _, timestamp, frames, list in
            deck.render(timestamp: timestamp, frames: Int(frames), into: list)
        }
        let gain = AVAudioUnitEQ(numberOfBands: 0)
        engine.attach(node)
        engine.attach(gain)
        engine.connect(node, to: gain, format: buffer.format)
        engine.connect(gain, to: mixer, format: buffer.format)

        let id = UUID()
        let voice = Voice(deck: deck, node: node, gain: gain, soundID: sound.id, title: sound.title)
        if fadeIn && fadeSeconds > 0 {
            voice.fadeValue = 0
            voice.fade = Fade(from: 0, to: 1, start: Date().timeIntervalSinceReferenceDate + max(0, delay),
                              duration: fadeSeconds, stopAtEnd: false)
        }
        applyGain(voice)
        voices[id] = voice
        if !engine.isRunning {
            do { try engine.start() } catch { finish(id); return false }
        }
        progress[sound.id] = 0
        startTimer()
        tick()
        return true
    }

    private func buffer(for url: URL) -> AVAudioPCMBuffer? {
        if let b = decoded[url] { return b }
        guard let file = try? AVAudioFile(forReading: url), file.length > 0,
              let b = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)),
              (try? file.read(into: b)) != nil, b.frameLength > 0 else { return nil }
        decoded[url] = b
        decodedOrder.removeAll { $0 == url }
        decodedOrder.append(url)
        if decodedOrder.count > 12 { decoded[decodedOrder.removeFirst()] = nil }
        return b
    }

    func stopAll() {
        for id in Array(voices.keys) { finish(id) }
    }

    func stop(_ id: UUID) { finish(id) }

    func isPlaying(_ sound: Sound) -> Bool { progress[sound.id] != nil }

    // MARK: Mischpult

    /// Lautstärke eines Sounds (0…200 %) – wird gemerkt und gilt für alle laufenden Kanäle dieses Sounds
    func setLevel(_ value: Double, for soundID: String) {
        let v = min(max(value, 0), Self.maxChannelLevel)
        levels[soundID] = abs(v - 1) < 0.001 ? nil : v
        for voice in voices.values where voice.soundID == soundID { applyGain(voice) }
        for i in channels.indices where channels[i].soundID == soundID { channels[i].level = v }
        if !applyingRemote { onChange?(.level(soundID: soundID, value: v)) }
    }

    /// Blendet einen Kanal aus und stoppt ihn danach
    func fadeOut(_ id: UUID) {
        guard let voice = voices[id] else { return }
        startFadeOut(id, voice)
        if !applyingRemote { onChange?(.fadeOut(soundID: voice.soundID)) }
        tick()
    }

    func fadeOutAll() {
        for id in Array(voices.keys) { fadeOut(id) }
    }

    private func startFadeOut(_ id: UUID, _ voice: Voice) {
        guard fadeSeconds > 0 else { finish(id); return }
        voice.fade = Fade(from: voice.fadeValue, to: 0, start: Date().timeIntervalSinceReferenceDate,
                          duration: fadeSeconds * voice.fadeValue, stopAtEnd: true)
    }

    // MARK: Plattenteller

    /// Hand auf den Teller: der Sound folgt nur noch dem Drehen
    func beginScratch(_ id: UUID) {
        guard let voice = voices[id] else { return }
        voice.deck.hold()
        if !applyingRemote { onChange?(.scratch(soundID: voice.soundID, position: voice.deck.seconds, holding: true)) }
    }

    /// Teller um so viele Sekunden weiterdrehen (negativ = zurück)
    func scratch(_ id: UUID, by seconds: Double) {
        guard let voice = voices[id] else { return }
        let target = voice.deck.move(by: seconds)
        if !applyingRemote { onChange?(.scratch(soundID: voice.soundID, position: target, holding: true)) }
    }

    /// Hand weg: läuft normal weiter
    func endScratch(_ id: UUID) {
        guard let voice = voices[id] else { return }
        voice.deck.release()
        if !applyingRemote { onChange?(.scratch(soundID: voice.soundID, position: voice.deck.seconds, holding: false)) }
    }

    // MARK: Aus der Lobby

    func apply(_ change: MixChange) {
        applyingRemote = true
        defer { applyingRemote = false }
        switch change {
        case let .settings(b, m, t, fi, fs):
            if bass != b { bass = b }
            if mid != m { mid = m }
            if treble != t { treble = t }
            if fadeIn != fi { fadeIn = fi }
            if fadeSeconds != fs { fadeSeconds = fs }
        case let .level(soundID, value):
            setLevel(value, for: soundID)
        case let .fadeOut(soundID):
            for (id, voice) in voices where voice.soundID == soundID && voice.fade?.stopAtEnd != true { startFadeOut(id, voice) }
            tick()
        case let .scratch(soundID, position, holding):
            // betrifft den zuletzt gestarteten Kanal dieses Sounds
            guard let voice = voices.values.filter({ $0.soundID == soundID }).max(by: { $0.started < $1.started }) else { return }
            if holding { voice.deck.hold(at: position) } else { voice.deck.release(at: position) }
        }
    }

    // MARK: Ablauf

    private func finish(_ id: UUID) {
        guard let voice = voices.removeValue(forKey: id) else { return }
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
            if voice.deck.isFinished { finished.append(id); continue }
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
            let p = voice.deck.progress
            next[voice.soundID] = max(next[voice.soundID] ?? 0, p)
            list.append(Channel(id: id, soundID: voice.soundID, title: voice.title, level: level(of: voice.soundID),
                                progress: p, position: voice.deck.seconds, fadingOut: voice.fade?.stopAtEnd == true))
        }
        progress = next
        if list != channels { channels = list }
    }
}

/// Ein Sound als „Platte“: liest aus dem dekodierten Puffer mit beliebiger Geschwindigkeit, auch rückwärts.
/// `render` läuft im Audio-Thread, alles Gemeinsame liegt hinter einem Lock.
private final class Deck: @unchecked Sendable {
    private let buffer: AVAudioPCMBuffer
    private let length: Int
    private let sampleRate: Double
    private let lock = OSAllocatedUnfairLock()

    private var position = 0.0          // in Frames
    private var startHostTime: UInt64   // 0 = sofort
    private var holding = false
    private var target = 0.0            // Teller-Position, solange die Hand drauf ist
    private var finished = false

    init(buffer: AVAudioPCMBuffer, startHostTime: UInt64) {
        self.buffer = buffer
        length = Int(buffer.frameLength)
        sampleRate = buffer.format.sampleRate
        self.startHostTime = startHostTime
    }

    var isFinished: Bool { lock.withLock { finished } }
    var seconds: Double { lock.withLock { position } / sampleRate }
    var progress: Double { lock.withLock { min(1, max(0, position / Double(max(1, length - 1)))) } }

    func hold(at seconds: Double? = nil) {
        lock.withLock {
            holding = true
            target = seconds.map { clamp($0 * sampleRate) } ?? position
        }
    }

    func move(by seconds: Double) -> Double {
        lock.withLock {
            if !holding { holding = true; target = position }
            target = clamp(target + seconds * sampleRate)
            return target / sampleRate
        }
    }

    func release(at seconds: Double? = nil) {
        lock.withLock {
            if let seconds { position = clamp(seconds * sampleRate) }
            holding = false
        }
    }

    private func clamp(_ frame: Double) -> Double { min(max(frame, 0), Double(length - 1)) }

    func render(timestamp: UnsafePointer<AudioTimeStamp>, frames: Int, into list: UnsafeMutablePointer<AudioBufferList>) -> OSStatus {
        let out = UnsafeMutableAudioBufferListPointer(list)
        for b in out { if let d = b.mData { memset(d, 0, Int(b.mDataByteSize)) } }
        guard let source = buffer.floatChannelData, frames > 0 else { return noErr }
        let sourceChannels = Int(buffer.format.channelCount)

        lock.lock()
        defer { lock.unlock() }
        if finished { return noErr }

        // Verzögerter Start (Lobby): bis dahin Stille, im richtigen Puffer ab dem passenden Frame
        var first = 0
        if startHostTime != 0 {
            let ts = timestamp.pointee
            if ts.mFlags.contains(.hostTimeValid) {
                let now = ts.mHostTime
                if startHostTime > now {
                    let wait = Int(AVAudioTime.seconds(forHostTime: startHostTime - now) * sampleRate)
                    if wait >= frames { return noErr }
                    first = wait
                }
            }
            startHostTime = 0
        }

        // Hand auf dem Teller: in diesem Puffer genau bis zur Teller-Position laufen (ergibt das Scratch-Geräusch)
        let count = frames - first
        let rate = holding ? max(-8, min(8, (target - position) / Double(count))) : 1
        var pos = position
        for f in first..<frames {
            let i = Int(pos)
            if i >= length - 1 {
                if !holding { finished = true }
                break
            }
            if pos >= 0 {
                let frac = Float(pos - Double(i))
                for c in 0..<out.count {
                    guard let d = out[c].mData?.assumingMemoryBound(to: Float.self) else { continue }
                    let s = source[min(c, sourceChannels - 1)]
                    d[f] = s[i] + (s[i + 1] - s[i]) * frac
                }
            }
            pos += rate
        }
        position = clamp(pos)
        return noErr
    }
}
