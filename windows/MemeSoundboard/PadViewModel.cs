using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace MemeSoundboard;

/// <summary>Gleiche Farbverläufe wie die Web- und Mac-Version.</summary>
public static class Palette
{
    private static readonly (uint, uint)[] Gradients =
    {
        (0xFF5F6D, 0xFFC371), (0x7F7FD5, 0x86A8E7), (0x11998E, 0x38EF7D), (0xFC466B, 0x3F5EFB),
        (0xF7971E, 0xFFB800), (0x00C6FF, 0x0072FF), (0xEE0979, 0xFF6A00), (0x8E2DE2, 0x4A00E0),
    };

    public static Color FromHex(uint hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

    /// <summary>Gleicher Hash wie im Web: h = h*31 + Zeichencode</summary>
    public static (Color, Color) GradientFor(string name)
    {
        uint h = 0;
        foreach (var rune in name.EnumerateRunes())
            unchecked { h = h * 31 + rune.ToString()[0]; }
        var g = Gradients[h % (uint)Gradients.Length];
        return (FromHex(g.Item1), FromHex(g.Item2));
    }
}

public sealed class PadViewModel : INotifyPropertyChanged
{
    public PadViewModel(Sound sound, string? key)
    {
        Sound = sound;
        Key = key;
        var (c1, c2) = Palette.GradientFor(sound.FileName);
        Glow = c1;
        Background = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
        Background.Freeze();
    }

    public Sound Sound { get; }
    public string Title => Sound.Title;
    public string FileName => Sound.FileName;
    public string? Key { get; }
    public Visibility KeyVisibility => Key is null ? Visibility.Collapsed : Visibility.Visible;
    public Brush Background { get; }
    public Color Glow { get; }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    private bool _confirmDelete;
    public bool ConfirmDelete
    {
        get => _confirmDelete;
        set { if (Set(ref _confirmDelete, value)) OnPropertyChanged(nameof(DeleteLabel)); }
    }
    public string DeleteLabel => ConfirmDelete ? "Löschen?" : "✕";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
