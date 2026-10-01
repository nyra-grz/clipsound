using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipSound;

public sealed class TileViewModel : INotifyPropertyChanged
{
    // Feste Farbe pro Sound (wie die Systemfarben in der Mac-Version)
    private static readonly Color[] Tints =
    {
        Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x88, 0x64, 0xD9), Color.FromRgb(0xE3, 0x00, 0x8C), Color.FromRgb(0xF7, 0x63, 0x0C),
        Color.FromRgb(0x10, 0x89, 0x3E), Color.FromRgb(0x00, 0x99, 0xBC), Color.FromRgb(0x5C, 0x2E, 0x91), Color.FromRgb(0xD1, 0x34, 0x38),
    };

    public TileViewModel(Sound sound)
    {
        Sound = sound;
        uint h = 0;
        foreach (char c in sound.FileName) unchecked { h = h * 31 + c; }
        Tint = new SolidColorBrush(Tints[h % (uint)Tints.Length]);
        Tint.Freeze();
    }

    public Sound Sound { get; }
    public string Title => Sound.Title;
    public string FileName => Sound.FileName;
    public Brush Tint { get; }

    private KeyBind? _bind;
    public KeyBind? Bind
    {
        get => _bind;
        set { _bind = value; Changed(); Changed(nameof(KeyText)); Changed(nameof(HasKey)); }
    }
    public string KeyText => Bind?.Display ?? "";
    public bool HasKey => Bind is not null;

    /// <summary>"global", "failed" oder "local"</summary>
    private string _hotKeyState = "local";
    public string HotKeyState
    {
        get => _hotKeyState;
        set { _hotKeyState = value; Changed(); Changed(nameof(KeyToolTip)); }
    }
    public string KeyToolTip => HotKeyState switch
    {
        "global" => "Funktioniert auch im Hintergrund · Klicken zum Ändern",
        "failed" => "Diese Kombination nutzt schon eine andere App – nur im ClipSound-Fenster aktiv · Klicken zum Ändern",
        _ => "Nur im ClipSound-Fenster · Klicken zum Ändern",
    };

    private double _progress;
    public double Progress { get => _progress; set { if (_progress != value) { _progress = value; Changed(); } } }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set { if (_isPlaying != value) { _isPlaying = value; Changed(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Kachel-Raster: so viele Spalten wie in die aktuelle Breite passen, alle gleich breit.
/// Rechnet bei jeder Fenstergröße neu (ersetzt das UniformGrid, bei dem sich Kacheln überlappt haben).
/// </summary>
public sealed class TileGrid : Panel
{
    public double MinTileWidth { get; set; } = 180;
    public double TileHeight { get; set; } = 86;
    public double Spacing { get; set; } = 8;

    private int Columns(double width) =>
        double.IsInfinity(width) || width <= 0 ? 1 : Math.Max(1, (int)((width + Spacing) / (MinTileWidth + Spacing)));

    private double TileWidth(double width, int cols) =>
        double.IsInfinity(width) ? MinTileWidth : Math.Max(0, (width - Spacing * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double w = TileWidth(available.Width, cols);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(w, TileHeight));
        int rows = (InternalChildren.Count + cols - 1) / cols;
        double width = double.IsInfinity(available.Width) ? cols * w + (cols - 1) * Spacing : available.Width;
        return new Size(width, rows == 0 ? 0 : rows * TileHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double w = TileWidth(final.Width, cols);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int row = i / cols, col = i % cols;
            InternalChildren[i].Arrange(new Rect(col * (w + Spacing), row * (TileHeight + Spacing), w, TileHeight));
        }
        return final;
    }
}

/// <summary>Fortschrittsbalken (0…1) in der Kachelfarbe.</summary>
public sealed class ProgressLine : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProgressLine),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(ActualHeight, ActualWidth * Math.Clamp(Progress, 0, 1));
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, w, ActualHeight), ActualHeight / 2, ActualHeight / 2);
    }
}
