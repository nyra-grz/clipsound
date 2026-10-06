import AudioToolbox
import CoreAudio
import Foundation

/// Lautstärke der echten Lautsprecher (Standard-Ausgabegerät), 0…1 – nicht nur die der App.
enum SystemVolume {
    private static var outputDevice: AudioDeviceID? {
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id)
        return status == noErr && id != 0 ? id : nil
    }

    private static var volumeAddress = AudioObjectPropertyAddress(mSelector: kAudioHardwareServiceDeviceProperty_VirtualMainVolume,
                                                                  mScope: kAudioDevicePropertyScopeOutput,
                                                                  mElement: kAudioObjectPropertyElementMain)

    /// nil, wenn das Gerät keine Lautstärke hat (z. B. manche HDMI-Ausgänge)
    static func get() -> Double? {
        guard let device = outputDevice, AudioObjectHasProperty(device, &volumeAddress) else { return nil }
        var volume = Float32(0)
        var size = UInt32(MemoryLayout<Float32>.size)
        guard AudioObjectGetPropertyData(device, &volumeAddress, 0, nil, &size, &volume) == noErr else { return nil }
        return Double(volume)
    }

    @discardableResult
    static func set(_ level: Double) -> Bool {
        guard let device = outputDevice, AudioObjectHasProperty(device, &volumeAddress) else { return false }
        var volume = Float32(min(1, max(0, level)))
        let ok = AudioObjectSetPropertyData(device, &volumeAddress, 0, nil, UInt32(MemoryLayout<Float32>.size), &volume) == noErr
        if ok && volume > 0 { unmute(device) }
        return ok
    }

    /// Wer lauter gestellt wird, soll auch etwas hören – Stummschaltung aufheben
    private static func unmute(_ device: AudioDeviceID) {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyMute,
                                                 mScope: kAudioDevicePropertyScopeOutput,
                                                 mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(device, &address) else { return }
        var mute = UInt32(0)
        AudioObjectSetPropertyData(device, &address, 0, nil, UInt32(MemoryLayout<UInt32>.size), &mute)
    }
}
