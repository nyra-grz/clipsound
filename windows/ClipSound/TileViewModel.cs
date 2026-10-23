using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipSound;

public sealed class TileViewModel : INotifyPropertyChanged
{
    /// <param name="lobby">Sound des Hosts, wenn man als Gast in einer Lobby ist</param>
    public TileViewModel(Sound sound, LobbySound? lobby = null)
    {
        Sound = sound;
        Lobby = lobby;
        // Farbe nach dem Originalnamen, damit Host und Gast dieselbe sehen (und gleich wie am Mac)
        Color = Palette.For(lobby?.Name ?? sound.FileName);
        Tint = Palette.Solid(Color);
        TileBrush = Palette.Tile(Color);
    }

    public Sound Sound { get; }
    public LobbySound? Lobby { get; }
    public string Title => Lobby?.Title ?? Sound.Title;
    /// <summary>Gast: Datei wird noch geladen</summary>
    public bool IsLoading => Lobby is not null && Lobby.Local is null;
    public string FileName => Sound.FileName;
    public Color Color { get; }
    public Brush Tint { get; }
    /// <summary>Hintergrund der Kachel (Farbverlauf)</summary>
    public Brush TileBrush { get; }
    public bool IsLobby => Lobby is not null;

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
    public static readonly DependencyProperty MinTileWidthProperty = DependencyProperty.Register(
        nameof(MinTileWidth), typeof(double), typeof(TileGrid), new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(TileGrid), new FrameworkPropertyMetadata(86.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Kachelgröße kommt als DynamicResource und ändert sich mit „Darstellung“</summary>
    public double MinTileWidth { get => (double)GetValue(MinTileWidthProperty); set => SetValue(MinTileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
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

/// <summary>Fortschrittsbalken (0…1), z. B. weiß auf der Kachel oder in der Kachelfarbe in der Wiedergabeleiste.</summary>
public sealed class ProgressLine : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProgressLine),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ProgressLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Hintergrund der ganzen Linie (leer = keiner)</summary>
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (Track is not null) dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, ActualHeight), ActualHeight / 2, ActualHeight / 2);
        double w = Math.Max(ActualHeight, ActualWidth * Math.Clamp(Progress, 0, 1));
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, w, ActualHeight), ActualHeight / 2, ActualHeight / 2);
    }
}
