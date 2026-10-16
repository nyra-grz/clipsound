using System.Diagnostics;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipSound;

/// <summary>
/// Spielt Sounds über einen gemeinsamen Mixer ab. Die Master-Lautstärke geht bis 500 %.
/// Ausgabe über PulseAudio (auch PipeWire) oder ALSA – eins davon hat jedes Linux mit Ton.
/// Fehlt beim Start ein Gerät oder fällt es weg, versucht es ClipSound beim nächsten Abspielen erneut.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    public const double MaxVolume = 5.0;
    public const int Rate = 44100;

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    private readonly MixingSampleProvider _mixer = new(MixFormat) { ReadFully = true };
    private readonly GainProvider _master;
    private readonly List<Voice> _voices = new();
    private readonly object _lock = new();
    private readonly object _outLock = new();
    private AudioOutput? _output;
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Gerade ist ein Ausgabegerät da</summary>
    public bool HasOutput { get { lock (_outLock) return _output is not null; } }
    /// <summary>Warum es keine Ausgabe gibt, bzw. welche gerade läuft</summary>
    public string? OutputError { get; private set; }
    public string OutputName { get { lock (_outLock) return _output?.Name ?? "keine"; } }

    public SoundPlayer()
    {
        _master = new GainProvider(_mixer);
        TryStartOutput();
    }

    public double Volume
    {
        get => _master.Gain;
        set => _master.Gain = (float)Math.Clamp(value, 0, MaxVolume);
    }

    private bool TryStartOutput()
    {
        lock (_outLock)
        {
            if (_disposed) return false;
            if (_output is not null) return true;
            var output = AudioOutput.Open(Rate, out var error);
            if (output is null) { OutputError = error; return false; }
            _output = output;
            OutputError = null;
            _thread = new Thread(() => Pump(output)) { IsBackground = true, Name = "ClipSound Audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return true;
        }
    }

    /// <summary>Mixer lesen und an das Gerät schreiben – das Schreiben blockiert und gibt so den Takt vor.</summary>
    private void Pump(AudioOutput output)
    {
        var buffer = new float[Rate / 100 * 2]; // 10 ms Stereo
        try
        {
            while (true)
            {
                lock (_outLock) if (_output != output) break;
                _master.Read(buffer, 0, buffer.Length);
                if (!output.Write(buffer)) break; // Gerät weg o. Ä.
            }
        }
        catch { /* gleich unten aufräumen */ }
        lock (_outLock) { if (_output == output) _output = null; }
        try { output.Dispose(); } catch { }
        // Neu versuchen (z. B. Bluetooth-Kopfhörer kurz weg); klappt es nicht, laufende Sounds beenden
        if (!_disposed) { Thread.Sleep(300); if (!TryStartOutput()) StopAll(); }
    }

    /// <summary>
    /// Öffnet eine Datei als Sample-Quelle. MP3, WAV, OGG und AIFF dekodiert ClipSound selbst (überall gleich),
    /// alles andere übernimmt ffmpeg, wenn es installiert ist.
    /// </summary>
    public static ISampleProvider Open(string path, out IDisposable reader, out long totalSamples, TimeSpan? limit = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".mp3":
            {
                var mp3 = new Mp3FileReaderBase(path, wf => new NLayer.NAudioSupport.Mp3FrameDecompressor(wf));
                reader = mp3;
                totalSamples = mp3.Length / Math.Max(1, mp3.WaveFormat.BitsPerSample / 8);
                return mp3.ToSampleProvider();
            }
            case ".wav":
            {
                var wav = new WaveFileReader(path);
                reader = wav;
                totalSamples = wav.SampleCount * wav.WaveFormat.Channels;
                return wav.ToSampleProvider();
            }
            case ".aif" or ".aiff":
            {
                var aiff = new AiffFileReader(path);
                reader = aiff;
                totalSamples = aiff.Length / Math.Max(1, aiff.WaveFormat.BitsPerSample / 8);
                return aiff.ToSampleProvider();
            }
            case ".ogg":
                try
                {
                    var vorbis = new VorbisWaveReader(path);
                    reader = vorbis;
                    totalSamples = vorbis.Length / (vorbis.WaveFormat.BitsPerSample / 8);
                    return vorbis;
                }
                catch { break; } // z. B. Opus in OGG → ffmpeg
        }
        var pcm = Ffmpeg.Decode(path, Rate, limit);
        var raw = new RawSourceWaveStream(new MemoryStream(pcm), MixFormat);
        reader = raw;
        totalSamples = pcm.Length / 4;
        return raw.ToSampleProvider();
    }

    /// <summary>Prüft, ob sich die Datei wirklich dekodieren lässt. Gibt null oder den Grund zurück.</summary>
    public static string? CheckDecodable(string path)
    {
        try
        {
            var source = Open(path, out var reader, out var total, TimeSpan.FromSeconds(1));
            using (reader)
            {
                if (total <= 0) return "leer";
                var buffer = new float[4096];
                return source.Read(buffer, 0, buffer.Length) > 0 ? null : "keine Audiodaten";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <param name="delay">Start erst nach dieser Zeit (Lobby: alle starten gleichzeitig)</param>
    public void Play(Sound sound, TimeSpan delay = default)
    {
        if (!TryStartOutput()) throw new InvalidOperationException("Kein Audiogerät gefunden" + (OutputError is null ? "." : $" ({OutputError})."));
        var source = Open(sound.Path, out var reader, out var total);

        // Auf das Mixer-Format bringen: Kanäle und Abtastrate
        long totalOut = total;
        if (source.WaveFormat.Channels == 1)
        {
            source = new MonoToStereoSampleProvider(source);
            totalOut *= 2;
        }
        else if (source.WaveFormat.Channels > 2)
        {
            source = new MultiplexingSampleProvider(new[] { source }, 2); // nur die ersten beiden Kanäle
            totalOut = totalOut / Math.Max(1, reader is WaveStream ws ? ws.WaveFormat.Channels : 2) * 2;
        }
        if (source.WaveFormat.SampleRate != MixFormat.SampleRate)
        {
            totalOut = (long)(totalOut * (double)MixFormat.SampleRate / source.WaveFormat.SampleRate);
            source = new WdlResamplingSampleProvider(source, MixFormat.SampleRate);
        }

        if (delay > TimeSpan.FromMilliseconds(5))
        {
            // Stille davor statt Timer: startet auf die Millisekunde genau im Mixer
            source = new OffsetSampleProvider(source) { DelayBy = delay };
            totalOut += (long)(delay.TotalSeconds * MixFormat.SampleRate) * MixFormat.Channels;
        }
        var voice = new Voice(sound.FileName, source, reader, Math.Max(1, totalOut));
        voice.Ended += () => OnVoiceEnded(voice);
        lock (_lock) _voices.Add(voice);
        _mixer.AddMixerInput(voice);
    }

    public void StopAll()
    {
        Voice[] all;
        lock (_lock) { all = _voices.ToArray(); }
        foreach (var v in all)
        {
            _mixer.RemoveMixerInput(v);
            v.Stop();
        }
    }

    /// <summary>Fortschritt (0…1) pro laufendem Sound.</summary>
    public Dictionary<string, double> Progress()
    {
        var result = new Dictionary<string, double>();
        lock (_lock)
        {
            foreach (var v in _voices)
                result[v.SoundId] = Math.Max(result.GetValueOrDefault(v.SoundId), v.Progress);
        }
        return result;
    }

    private void OnVoiceEnded(Voice voice)
    {
        lock (_lock) { if (!_voices.Remove(voice)) return; }
        voice.DisposeReader();
    }

    public void Dispose()
    {
        AudioOutput? output;
        lock (_outLock) { _disposed = true; output = _output; _output = null; }
        StopAll();
        _thread?.Join(500);
        try { output?.Dispose(); } catch { }
    }

    /// <summary>Ein laufender Sound. Meldet sich ab, wenn die Datei zu Ende ist.</summary>
    private sealed class Voice : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly IDisposable _reader;
        private readonly long _total;
        private long _read;
        private int _ended;

        public Voice(string soundId, ISampleProvider source, IDisposable reader, long total)
        {
            SoundId = soundId; _source = source; _reader = reader; _total = total;
        }

        public string SoundId { get; }
        public WaveFormat WaveFormat => _source.WaveFormat;
        public double Progress => Math.Clamp((double)Interlocked.Read(ref _read) / _total, 0, 1);
        public event Action? Ended;

        public int Read(float[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _ended) == 1) return 0;
            int n;
            // Eine kaputte Stelle in einer Datei darf nicht die ganze Ausgabe anhalten
            try { n = _source.Read(buffer, offset, count); }
            catch { Stop(); return 0; }
            Interlocked.Add(ref _read, n);
            if (n < count) Stop(); // Datei zu Ende – der Mixer entfernt uns
            return n;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) Ended?.Invoke();
        }

        public void DisposeReader()
        {
            try { _reader.Dispose(); } catch { /* egal */ }
        }
    }

    /// <summary>Verstärkung ohne Obergrenze bei 1.0; begrenzt das Ergebnis auf -1…1 gegen Überlauf.</summary>
    private sealed class GainProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        public GainProvider(ISampleProvider source) => _source = source;
        public float Gain { get; set; } = 0.8f;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            float g = Gain;
            for (int i = offset; i < offset + n; i++)
                buffer[i] = Math.Clamp(buffer[i] * g, -1f, 1f);
            return n;
        }
    }
}

/// <summary>Für Formate, die ClipSound nicht selbst dekodiert (FLAC, M4A, AAC, Opus …).</summary>
public static class Ffmpeg
{
    public static bool Available => Find() is not null;

    private static string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            var p = Path.Combine(dir, "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Datei zu 32-Bit-Float, Stereo, <paramref name="rate"/> Hz dekodieren (Sounds sind kurz – alles in den Speicher).</summary>
    public static byte[] Decode(string path, int rate, TimeSpan? limit)
    {
        var ffmpeg = Find() ?? throw new InvalidDataException("für dieses Format braucht ClipSound ffmpeg (z. B. sudo apt install ffmpeg)");
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", path }) psi.ArgumentList.Add(a);
        if (limit is { } l) { psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(l.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        foreach (var a in new[] { "-f", "f32le", "-ac", "2", "-ar", rate.ToString(), "pipe:1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidDataException("ffmpeg ließ sich nicht starten");
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit(30000);
        if (ms.Length == 0) throw new InvalidDataException("ffmpeg: " + (err.Result.Trim() is { Length: > 0 } e ? e.Split('\n')[0] : "keine Audiodaten"));
        return ms.ToArray();
    }
}
