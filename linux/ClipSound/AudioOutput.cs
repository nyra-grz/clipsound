using System.Runtime.InteropServices;

namespace ClipSound;

/// <summary>
/// Ton ausgeben – zuerst über PulseAudio (das bedient auch PipeWire, Standard bei Ubuntu, Fedora, Mint …),
/// sonst direkt über ALSA. Beide Bibliotheken sind auf praktisch jedem Linux mit Ton vorhanden.
/// Schreibt 32-Bit-Float, Stereo; Write blockiert, bis Platz im Puffer ist.
/// </summary>
public abstract class AudioOutput : IDisposable
{
    public abstract string Name { get; }
    /// <summary>false = Gerät weg / Fehler (dann neu öffnen)</summary>
    public abstract bool Write(float[] samples);
    public abstract void Dispose();

    public static AudioOutput? Open(int rate, out string? error)
    {
        var problems = new List<string>();
        foreach (var make in new Func<AudioOutput>[] { () => new PulseOutput(rate), () => new AlsaOutput(rate) })
        {
            try { error = null; return make(); }
            catch (Exception ex) { problems.Add(ex.Message); }
        }
        error = string.Join("; ", problems);
        return null;
    }
}

/// <summary>PulseAudio „simple“-Schnittstelle (libpulse-simple.so.0)</summary>
public sealed class PulseOutput : AudioOutput
{
    private const string Lib = "libpulse-simple.so.0";
    private IntPtr _handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr { public uint MaxLength, TLength, Prebuf, MinReq, FragSize; }

    [DllImport(Lib)] private static extern IntPtr pa_simple_new(string? server, string name, int dir, string? dev, string streamName,
        ref SampleSpec spec, IntPtr map, ref BufferAttr attr, out int error);
    [DllImport(Lib)] private static extern int pa_simple_write(IntPtr s, float[] data, UIntPtr bytes, out int error);
    [DllImport(Lib)] private static extern void pa_simple_free(IntPtr s);
    [DllImport("libpulse.so.0")] private static extern IntPtr pa_strerror(int error);

    public PulseOutput(int rate)
    {
        var spec = new SampleSpec { Format = 5 /* PA_SAMPLE_FLOAT32LE */, Rate = (uint)rate, Channels = 2 };
        uint tlength = (uint)(rate * 2 * 4 * 0.04); // ~40 ms Puffer: kurz genug, dass Tasten sofort reagieren
        var attr = new BufferAttr { MaxLength = uint.MaxValue, TLength = tlength, Prebuf = uint.MaxValue, MinReq = tlength / 4, FragSize = uint.MaxValue };
        _handle = pa_simple_new(null, "ClipSound", 1 /* PA_STREAM_PLAYBACK */, null, "Sounds", ref spec, IntPtr.Zero, ref attr, out int error);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("PulseAudio: " + Describe(error));
    }

    public override string Name => "PulseAudio/PipeWire";

    public override bool Write(float[] samples)
    {
        if (_handle == IntPtr.Zero) return false;
        return pa_simple_write(_handle, samples, (UIntPtr)(samples.Length * 4), out _) == 0;
    }

    public override void Dispose()
    {
        if (_handle != IntPtr.Zero) { pa_simple_free(_handle); _handle = IntPtr.Zero; }
    }

    private static string Describe(int error)
    {
        try { return Marshal.PtrToStringAnsi(pa_strerror(error)) ?? $"Fehler {error}"; } catch { return $"Fehler {error}"; }
    }
}

/// <summary>ALSA direkt (libasound.so.2), Gerät „default“ – für Systeme ohne PulseAudio/PipeWire</summary>
public sealed class AlsaOutput : AudioOutput
{
    private const string Lib = "libasound.so.2";
    private IntPtr _pcm;

    [DllImport(Lib)] private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);
    [DllImport(Lib)] private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyUs);
    [DllImport(Lib)] private static extern IntPtr snd_pcm_writei(IntPtr pcm, float[] buffer, UIntPtr frames);
    [DllImport(Lib)] private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);
    [DllImport(Lib)] private static extern int snd_pcm_close(IntPtr pcm);
    [DllImport(Lib)] private static extern IntPtr snd_strerror(int errnum);

    public AlsaOutput(int rate)
    {
        int err = snd_pcm_open(out _pcm, "default", 0 /* SND_PCM_STREAM_PLAYBACK */, 0);
        if (err < 0) throw new InvalidOperationException("ALSA: " + Describe(err));
        err = snd_pcm_set_params(_pcm, 14 /* SND_PCM_FORMAT_FLOAT_LE */, 3 /* SND_PCM_ACCESS_RW_INTERLEAVED */, 2, (uint)rate, 1, 50_000);
        if (err < 0) { snd_pcm_close(_pcm); _pcm = IntPtr.Zero; throw new InvalidOperationException("ALSA: " + Describe(err)); }
    }

    public override string Name => "ALSA";

    public override bool Write(float[] samples)
    {
        if (_pcm == IntPtr.Zero) return false;
        long n = (long)snd_pcm_writei(_pcm, samples, (UIntPtr)(samples.Length / 2));
        if (n >= 0) return true;
        // Unterlauf o. Ä.: ALSA wieder in Gang bringen; geht das nicht, ist das Gerät weg
        return snd_pcm_recover(_pcm, (int)n, 1) >= 0;
    }

    public override void Dispose()
    {
        if (_pcm != IntPtr.Zero) { snd_pcm_close(_pcm); _pcm = IntPtr.Zero; }
    }

    private static string Describe(int err)
    {
        try { return Marshal.PtrToStringAnsi(snd_strerror(err)) ?? $"Fehler {err}"; } catch { return $"Fehler {err}"; }
    }
}
