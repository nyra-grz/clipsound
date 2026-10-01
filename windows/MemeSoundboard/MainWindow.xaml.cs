using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MemeSoundboard;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Key[] ShortcutKeys =
    {
        Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0,
        Key.Q, Key.W, Key.E, Key.R, Key.T, Key.Z, Key.U, Key.I, Key.O, Key.P,
    };
    private static readonly string[] ShortcutLabels = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "Q", "W", "E", "R", "T", "Z", "U", "I", "O", "P" };

    private const double PadMinWidth = 160 + 12;

    private readonly SoundLibrary _library = new();
    private readonly SoundPlayer? _player;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Settings _settings = Settings.Load();
    private List<PadViewModel> _visible = new();
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        try
        {
            _player = new SoundPlayer();
        }
        catch (Exception ex)
        {
            Loaded += (_, _) => ShowToast("Kein Audiogerät gefunden: " + ex.Message, error: true);
        }

        Volume.Value = Math.Clamp(_settings.Volume, 0, SoundPlayer.MaxVolume);
        Overlap.IsChecked = _settings.Overlap;
        _loading = false;
        ApplyVolume();

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateProgress(), Dispatcher);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0); };

        Scroller.SizeChanged += (_, _) => UpdateColumns();
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible; };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) => Keyboard.Focus(this); // Tasten 1–0/Q–P sofort nutzbar
        Closing += (_, _) => { _settings.Save(); _player?.Dispose(); };

        Render();
    }

    // ---------- Spalten ----------

    private int _padColumns = 6;
    public int PadColumns
    {
        get => _padColumns;
        set { if (_padColumns != value) { _padColumns = value; PropertyChanged?.Invoke(this, new(nameof(PadColumns))); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void UpdateColumns()
    {
        double width = Scroller.ViewportWidth - 28;
        PadColumns = Math.Max(1, (int)(width / PadMinWidth));
    }

    // ---------- Liste ----------

    private void Render()
    {
        var q = Search.Text.Trim();
        var sounds = string.IsNullOrEmpty(q)
            ? _library.Sounds
            : _library.Sounds.Where(s => s.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        _visible = sounds.Select((s, i) => new PadViewModel(s, i < ShortcutLabels.Length ? ShortcutLabels[i] : null)).ToList();
        Pads.ItemsSource = _visible;

        int n = _library.Sounds.Count;
        CountText.Text = n == 1 ? "1 Sound" : $"{n} Sounds";
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgress();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Render();

    // ---------- Abspielen ----------

    private void Play(PadViewModel pad)
    {
        if (_player is null) { ShowToast("Kein Audiogerät gefunden", error: true); return; }
        if (Overlap.IsChecked != true) _player.StopAll();
        try
        {
            _player.Play(pad.Sound);
            pad.IsPlaying = true;
            _progressTimer.Start();
        }
        catch (Exception ex)
        {
            ShowToast($"„{pad.Title}“ konnte nicht abgespielt werden: {ex.Message}", error: true);
        }
    }

    private void StopAll()
    {
        _player?.StopAll();
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var progress = _player?.Progress() ?? new Dictionary<string, double>();
        foreach (var pad in _visible)
        {
            if (progress.TryGetValue(pad.FileName, out var p))
            {
                pad.IsPlaying = true;
                pad.Progress = p;
            }
            else if (pad.IsPlaying)
            {
                pad.IsPlaying = false;
                pad.Progress = 0;
            }
        }
        if (progress.Count == 0) _progressTimer?.Stop();
    }

    private void Pad_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PadViewModel pad } el) return;
        Play(pad);
        // kurzer "Drück"-Effekt
        if (el.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            scale = new ScaleTransform();
            el.RenderTransform = scale;
        }
        var anim = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        e.Handled = true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll();

    // ---------- Lautstärke & Optionen ----------

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyVolume();

    private void ApplyVolume()
    {
        if (_loading || VolumeText is null) return;
        if (_player is not null) _player.Volume = Volume.Value;
        VolumeText.Text = $"{Math.Round(Volume.Value * 100)} %";
        VolumeText.Foreground = Volume.Value > 1.0001 ? (Brush)FindResource("Danger") : (Brush)FindResource("Text");
        _settings.Volume = Volume.Value;
    }

    private void VolumeText_Click(object sender, MouseButtonEventArgs e) => Volume.Value = 1;

    private void Overlap_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) _settings.Overlap = Overlap.IsChecked == true;
    }

    // ---------- Import ----------

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        // Klick öffnet das kleine Menü: Dateien oder Ordner
        var menu = ImportBtn.ContextMenu!;
        menu.PlacementTarget = ImportBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", SoundLibrary.AudioExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog
        {
            Title = "Sounds importieren",
            Multiselect = true,
            Filter = $"Sounds ({exts})|{exts}|Alle Dateien (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) Import(dialog.FileNames);
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner mit Sounds auswählen", Multiselect = true };
        if (dialog.ShowDialog(this) == true) Import(dialog.FolderNames);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Import(paths);
    }

    private void Import(IEnumerable<string> paths)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        (int added, List<string> rejected) result;
        try { result = _library.Import(paths); }
        finally { Mouse.OverrideCursor = null; }

        Render();
        if (result.rejected.Count > 0)
        {
            var names = string.Join(", ", result.rejected.Take(3)) + (result.rejected.Count > 3 ? " …" : "");
            var prefix = result.added > 0 ? $"{result.added} importiert · " : "";
            ShowToast($"{prefix}Kein Sound: {names}", error: true);
        }
        else if (result.added > 0)
        {
            ShowToast($"{result.added} Sound{(result.added == 1 ? "" : "s")} importiert 🎉");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.Folder}\"") { UseShellExecute = true });

    // ---------- Löschen & Kontextmenü ----------

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PadViewModel pad }) return;
        e.Handled = true;
        if (!pad.ConfirmDelete)
        {
            pad.ConfirmDelete = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += (_, _) => { t.Stop(); pad.ConfirmDelete = false; };
            t.Start();
            return;
        }
        DeleteSound(pad);
    }

    private void DeleteSound(PadViewModel pad)
    {
        StopAll();
        _library.Delete(pad.Sound);
        Render();
        ShowToast("Gelöscht: " + pad.Title);
    }

    private static PadViewModel? PadFrom(object sender) => (sender as FrameworkElement)?.DataContext as PadViewModel;
    private void Menu_Play(object sender, RoutedEventArgs e) { if (PadFrom(sender) is { } p) Play(p); }
    private void Menu_Delete(object sender, RoutedEventArgs e) { if (PadFrom(sender) is { } p) DeleteSound(p); }
    private void Menu_Reveal(object sender, RoutedEventArgs e)
    {
        if (PadFrom(sender) is { } p)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p.Sound.Path}\"") { UseShellExecute = true });
    }

    // ---------- Tastatur ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.F) { Search.Focus(); Search.SelectAll(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.O) { ImportFiles_Click(this, e); e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            StopAll();
            if (Search.IsKeyboardFocused) Keyboard.Focus(this);
            e.Handled = true;
            return;
        }
        if (Search.IsKeyboardFocused)
        {
            if (e.Key == Key.Enter && _visible.Count > 0) { Play(_visible[0]); e.Handled = true; }
            return;
        }
        if (mods != ModifierKeys.None) return;
        int i = Array.IndexOf(ShortcutKeys, e.Key);
        if (i < 0) i = e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? (e.Key == Key.NumPad0 ? 9 : e.Key - Key.NumPad1) : -1;
        if (i >= 0 && i < _visible.Count) { Play(_visible[i]); e.Handled = true; }
    }

    // ---------- Toast ----------

    private void ShowToast(string text, bool error = false)
    {
        ToastText.Text = text;
        ToastText.Foreground = error ? new SolidColorBrush(Palette.FromHex(0xFFB3C0)) : (Brush)FindResource("Text");
        Toast.BorderBrush = error ? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x3B, 0x5C)) : (Brush)FindResource("Line");
        Fade(Toast, 1);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static void Fade(UIElement el, double to) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200)));

    // ---------- Dunkle Titelleiste (Windows 10 20H1+ / 11) ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            int caption = 0x00160E0C; // COLORREF (BGR) von #0C0E16
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { /* ältere Windows-Versionen: normale Titelleiste */ }
    }
}

/// <summary>Weißer Fortschrittsbalken (0…1).</summary>
public sealed class ProgressFill : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressFill),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth * Math.Clamp(Progress, 0, 1);
        if (w > 0) dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, w, ActualHeight), 2, 2);
    }
}

/// <summary>Lautstärke und Überlappen merken (%AppData%\Meme Soundboard\settings.json).</summary>
public sealed class Settings
{
    public double Volume { get; set; } = 0.8;
    public bool Overlap { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Meme Soundboard", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* egal */ }
    }
}
