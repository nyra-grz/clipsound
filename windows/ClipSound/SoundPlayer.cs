using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipSound;

/// <summary>
/// Spielt Sounds über einen gemeinsamen Mixer ab. Die Master-Lautstärke geht bis 500 %.
/// Die Ausgabe hängt am Standard-Audiogerät: Fehlt beim Start eins, wird es beim nächsten Abspielen
/// erneut versucht; fällt es weg (Kopfhörer, Bluetooth) oder wechselt Windows das Standardgerät,
/// startet die Ausgabe von selbst neu.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    public const double MaxVolume = 5.0;

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly MixingSampleProvider _mixer = new(MixFormat) { ReadFully = true };
    private readonly GainProvider _master;
    private readonly List<Voice> _voices = new();
    private readonly object _lock = new();
    private readonly object _outLock = new();
    private WaveOutEvent? _output;
    private bool _disposed;
    private MMDeviceEnumerator? _devices;
    private DeviceWatch? _watch;

    /// <summary>Wird (im Audio-Thread) ausgelöst, wenn ein Sound fertig ist.</summary>
    public event Action<string>? Finished;

    /// <summary>Gerade ist ein Ausgabegerät da</summary>
    public bool HasOutput { get { lock (_outLock) return _output is not null; } }
    /// <summary>Warum es keine Ausgabe gibt (z. B. kein Audiogerät)</summary>
    public string? OutputError { get; private set; }

    public SoundPlayer()
    {
        _master = new GainProvider(_mixer);
        TryStartOutput();
        try
        {
            // Standardgerät gewechselt (Kopfhörer rein, Bluetooth verbunden …) → dorthin umziehen
            _devices = new MMDeviceEnumerator();
            _watch = new DeviceWatch(() => Task.Run(RestartOutput));
            _devices.RegisterEndpointNotificationCallback(_watch);
        }
        catch { _devices = null; _watch = null; }
    }

    private bool TryStartOutput()
    {
        lock (_outLock)
        {
            if (_disposed) return false;
            if (_output is not null) return true;
            WaveOutEvent? output = null;
            try
            {
                output = new WaveOutEvent { DesiredLatency = 80 };
                output.Init(_master);
                var started = output;
                output.PlaybackStopped += (_, _) => OnOutputStopped(started);
                output.Play();
                _output = output;
                OutputError = null;
                return true;
            }
            catch (Exception ex)
            {
                try { output?.Dispose(); } catch { }
                OutputError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Gerät ist weg oder hat einen Fehler gemeldet: auf dem (neuen) Standardgerät weitermachen.</summary>
    private void OnOutputStopped(WaveOutEvent output)
    {
        lock (_outLock)
        {
            if (_output != output) return; // schon ersetzt
            _output = null;
        }
        Task.Run(() =>
        {
            try { output.Dispose(); } catch { }
            Thread.Sleep(300);
            if (!TryStartOutput()) StopAll(); // kein Gerät mehr: laufende Sounds nicht ewig „spielen“ lassen
        });
    }

    private void RestartOutput()
    {
        WaveOutEvent? old;
        lock (_outLock)
        {
            if (_disposed) return;
            old = _output;
            _output = null;
        }
        try { old?.Dispose(); } catch { }
        TryStartOutput();
    }

    public double Volume
    {
        get => _master.Gain;
        set => _master.Gain = (float)Math.Clamp(value, 0, MaxVolume);
    }

    public static ISampleProvider Open(string path, out IDisposable reader, out long totalSamples)
    {
        if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = new VorbisWaveReader(path);
            reader = vorbis;
            totalSamples = vorbis.Length / (vorbis.WaveFormat.BitsPerSample / 8);
            return vorbis;
        }
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            // MP3 zuerst mit NLayer (reiner C#-Decoder) – braucht keine Windows-Codecs,
            // die z. B. bei „N“-Editionen fehlen
            try
            {
                var mp3 = new Mp3FileReaderBase(path, wf => new NLayer.NAudioSupport.Mp3FrameDecompressor(wf));
                reader = mp3;
                totalSamples = mp3.Length / Math.Max(1, mp3.WaveFormat.BitsPerSample / 8);
                return mp3.ToSampleProvider();
            }
            catch { /* weiter mit den Windows-Decodern */ }
        }
        try
        {
            var file = new AudioFileReader(path);
            reader = file;
            totalSamples = file.Length / (file.WaveFormat.BitsPerSample / 8);
            return file;
        }
        catch (Exception first)
        {
            // Plan B: Windows' eigene Decoder (Media Foundation) – klappt bei MP3s, die der
            // Standard-Decoder nicht mag, und bei M4A/AAC/WMA/FLAC
            try
            {
                var mf = new MediaFoundationReader(path);
                reader = mf;
                totalSamples = mf.Length / Math.Max(1, mf.WaveFormat.BitsPerSample / 8);
                return mf.ToSampleProvider();
            }
            catch (Exception second)
            {
                throw new InvalidDataException($"{first.Message} / Media Foundation: {second.Message}", first);
            }
        }
    }

    /// <summary>Prüft, ob sich die Datei wirklich dekodieren lässt. Gibt null oder den Grund zurück.</summary>
    public static string? CheckDecodable(string path)
    {
        try
        {
            var source = Open(path, out var reader, out var total);
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
            throw new NotSupportedException("Nur Mono und Stereo werden unterstützt");
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
        bool stillPlaying;
        lock (_lock)
        {
            if (!_voices.Remove(voice)) return;
            stillPlaying = _voices.Any(v => v.SoundId == voice.SoundId);
        }
        voice.DisposeReader();
        if (!stillPlaying) Finished?.Invoke(voice.SoundId);
    }

    public void Dispose()
    {
        WaveOutEvent? output;
        lock (_outLock) { _disposed = true; output = _output; _output = null; }
        try { if (_watch is not null) _devices?.UnregisterEndpointNotificationCallback(_watch); } catch { }
        _devices?.Dispose();
        StopAll();
        output?.Dispose();
    }

    /// <summary>Meldet, wenn Windows das Standard-Ausgabegerät wechselt (kommt auf einem COM-Thread).</summary>
    private sealed class DeviceWatch(Action changed) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) changed();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
            if (n < count) Stop(); // Datei zu Ende – der Mixer entfernt uns bei 0
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
