import AppKit
import Carbon.HIToolbox

/// Eine Taste oder Tastenkombination. Gespeichert wird die physische Taste (keyCode),
/// damit globale Kürzel funktionieren und Z/Y auf QWERTZ stimmen.
struct KeyBind: Codable, Equatable {
    var keyCode: UInt16
    var modifiers: UInt   // NSEvent.ModifierFlags, nur ⌃⌥⇧⌘
    var label: String

    static let relevant: NSEvent.ModifierFlags = [.control, .option, .shift, .command]

    var flags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    /// Mit ⌃ funktioniert die Kombination auch, wenn ClipSound im Hintergrund ist
    var isGlobal: Bool { flags.contains(.control) }

    /// In Apples Reihenfolge: ⌃⌥⇧⌘
    var display: String {
        var s = ""
        if flags.contains(.control) { s += "⌃" }
        if flags.contains(.option) { s += "⌥" }
        if flags.contains(.shift) { s += "⇧" }
        if flags.contains(.command) { s += "⌘" }
        return s + label
    }

    init(keyCode: UInt16, flags: NSEvent.ModifierFlags = [], label: String? = nil) {
        self.keyCode = keyCode
        self.modifiers = flags.intersection(Self.relevant).rawValue
        self.label = label ?? Self.name(for: keyCode)
    }

    init(event: NSEvent) {
        self.init(keyCode: event.keyCode, flags: event.modifierFlags)
    }

    func matches(_ event: NSEvent) -> Bool {
        event.keyCode == keyCode && event.modifierFlags.intersection(Self.relevant).rawValue == modifiers
    }

    // MARK: Namen

    private static let specialNames: [UInt16: String] = [
        49: "Space", 36: "↩", 76: "⌤", 48: "⇥", 51: "⌫", 117: "⌦", 53: "Esc",
        123: "←", 124: "→", 125: "↓", 126: "↑", 115: "↖", 119: "↘", 116: "⇞", 121: "⇟",
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8",
        101: "F9", 109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15",
        82: "Num 0", 83: "Num 1", 84: "Num 2", 85: "Num 3", 86: "Num 4", 87: "Num 5",
        88: "Num 6", 89: "Num 7", 91: "Num 8", 92: "Num 9", 65: "Num ,", 67: "Num *",
        69: "Num +", 75: "Num /", 78: "Num -",
    ]

    /// Beschriftung der Taste im aktuellen Tastaturlayout (z. B. „Z“ auf QWERTZ)
    static func name(for keyCode: UInt16) -> String {
        if let special = specialNames[keyCode] { return special }
        return layoutCharacter(for: keyCode) ?? "#\(keyCode)"
    }

    private static func layoutCharacter(for keyCode: UInt16) -> String? {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let ptr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let data = Unmanaged<CFData>.fromOpaque(ptr).takeUnretainedValue() as Data
        var deadKeys: UInt32 = 0
        var chars = [UniChar](repeating: 0, count: 4)
        var length = 0
        let status = data.withUnsafeBytes { raw -> OSStatus in
            guard let layout = raw.bindMemory(to: UCKeyboardLayout.self).baseAddress else { return -1 }
            return UCKeyTranslate(layout, keyCode, UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                                  OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeys, chars.count, &length, &chars)
        }
        guard status == noErr, length > 0 else { return nil }
        let s = String(utf16CodeUnits: chars, count: length).uppercased()
        return s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : s
    }

    /// Standardtasten: Zahlenreihe 1–0, dann die obere Buchstabenreihe (Q W E R T Z U I O P)
    static let defaults: [UInt16] = [18, 19, 20, 21, 23, 22, 26, 28, 25, 29, 12, 13, 14, 15, 17, 16, 32, 34, 31, 35]
}

/// Speichert, welcher Sound welche Taste hat (keybinds.json neben dem Sound-Ordner).
final class KeyBindStore: ObservableObject {
    @Published private(set) var binds: [String: KeyBind] = [:]
    /// Sounds, deren Taste bewusst entfernt wurde – bekommen keine Standardtaste mehr
    private var unbound: Set<String> = []
    private let file: URL

    private struct Saved: Codable {
        var binds: [String: KeyBind]
        var unbound: [String]
    }

    init(file: URL) {
        self.file = file
        if let data = try? Data(contentsOf: file), let saved = try? JSONDecoder().decode(Saved.self, from: data) {
            binds = saved.binds
            unbound = Set(saved.unbound)
        }
    }

    /// Neue Sounds bekommen die nächste freie Standardtaste, gelöschte verlieren ihre.
    func sync(with sounds: [Sound]) {
        let ids = Set(sounds.map(\.id))
        var next = binds.filter { ids.contains($0.key) }
        unbound = unbound.intersection(ids)
        var free = KeyBind.defaults.filter { code in !next.values.contains { $0.keyCode == code && $0.modifiers == 0 } }
        for sound in sounds where next[sound.id] == nil && !unbound.contains(sound.id) {
            guard !free.isEmpty else { break }
            next[sound.id] = KeyBind(keyCode: free.removeFirst())
        }
        if next != binds { binds = next; save() }
    }

    /// Setzt die Taste. Gibt die ID des Sounds zurück, der sie vorher hatte.
    @discardableResult
    func set(_ bind: KeyBind?, for id: String) -> String? {
        var previousOwner: String?
        if let bind {
            if let owner = binds.first(where: { $0.key != id && $0.value == bind })?.key {
                binds[owner] = nil
                unbound.insert(owner)
                previousOwner = owner
            }
            binds[id] = bind
            unbound.remove(id)
        } else {
            binds[id] = nil
            unbound.insert(id)
        }
        save()
        return previousOwner
    }

    func soundID(for event: NSEvent) -> String? {
        binds.first { $0.value.matches(event) }?.key
    }

    private func save() {
        let saved = Saved(binds: binds, unbound: unbound.sorted())
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? encoder.encode(saved).write(to: file, options: .atomic)
    }
}

/// Systemweite Kürzel über Carbon (braucht keine Bedienungshilfen-Freigabe).
final class GlobalHotKeys {
    static let shared = GlobalHotKeys()

    var onPress: ((String) -> Void)?
    private(set) var active: Set<String> = []
    private(set) var failed: Set<String> = []
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var soundIDs: [UInt32: String] = [:]

    private init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hotKey = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKey)
            let id = hotKey.id
            DispatchQueue.main.async { GlobalHotKeys.shared.fire(id) }
            return noErr
        }, 1, &spec, nil, nil)
    }

    func register(_ binds: [String: KeyBind]) {
        unregisterAll()
        var next: UInt32 = 1
        for (soundID, bind) in binds.sorted(by: { $0.key < $1.key }) where bind.isGlobal {
            var ref: EventHotKeyRef?
            let hotKeyID = EventHotKeyID(signature: OSType(0x434C_5053), id: next) // "CLPS"
            let status = RegisterEventHotKey(UInt32(bind.keyCode), Self.carbonModifiers(bind.flags), hotKeyID,
                                             GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref {
                refs[next] = ref
                soundIDs[next] = soundID
                active.insert(soundID)
            } else {
                failed.insert(soundID) // z. B. eventHotKeyExistsErr: eine andere App nutzt die Kombination
            }
            next += 1
        }
    }

    func unregisterAll() {
        refs.values.forEach { UnregisterEventHotKey($0) }
        refs.removeAll()
        soundIDs.removeAll()
        active.removeAll()
        failed.removeAll()
    }

    private func fire(_ id: UInt32) {
        if let soundID = soundIDs[id] { onPress?(soundID) }
    }

    private static func carbonModifiers(_ flags: NSEvent.ModifierFlags) -> UInt32 {
        var m: UInt32 = 0
        if flags.contains(.control) { m |= UInt32(controlKey) }
        if flags.contains(.option) { m |= UInt32(optionKey) }
        if flags.contains(.shift) { m |= UInt32(shiftKey) }
        if flags.contains(.command) { m |= UInt32(cmdKey) }
        return m
    }
}
