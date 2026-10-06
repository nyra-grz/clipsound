using NAudio.CoreAudioApi;

namespace ClipSound;

/// <summary>Lautstärke der echten Lautsprecher (Standard-Ausgabegerät), 0…1 – nicht nur die der App.</summary>
public static class SystemVolume
{
    public static double? Get()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch { return null; } // kein Ausgabegerät
    }

    public static bool Set(double level)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var v = (float)Math.Clamp(level, 0, 1);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = v;
            if (v > 0) device.AudioEndpointVolume.Mute = false; // wer lauter gestellt wird, soll auch was hören
            return true;
        }
        catch { return false; }
    }
}
