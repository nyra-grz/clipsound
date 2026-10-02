using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipSound;

/// <summary>
/// Spielt Sounds über einen gemeinsamen Mixer ab. Die Master-Lautstärke geht bis 500 %.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    public const double MaxVolume = 5.0;

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private readonly MixingSampleProvider _mixer = new(MixFormat) { ReadFully = true };
    private readonly GainProvider _master;
    private readonly WaveOutEvent _output = new() { DesiredLatency = 80 };
    private readonly List<Voice> _voices = new();
    private readonly object _lock = new();

    /// <summary>Wird (im Audio-Thread) ausgelöst, wenn ein Sound fertig ist.</summary>
    public event Action<string>? Finished;

    public SoundPlayer()
    {
        _master = new GainProvider(_mixer);
        _output.Init(_master);
        _output.Play();
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

    public void Play(Sound sound)
    {
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
        StopAll();
        _output.Dispose();
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
            int n = _source.Read(buffer, offset, count);
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
