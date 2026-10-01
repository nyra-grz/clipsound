using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClipSound;

// Startargumente (zum Testen):
//   --library <ordner>   anderen Sound-Ordner benutzen
//   --size <B>x<H>       Fenstergröße (z. B. 700x600)
//   --recorder           Aufnahmefenster für den ersten Sound öffnen
//   --snapshot <png>     Fenster als Bild speichern und beenden
//   --selftest <txt>     Tastenkürzel und Wiedergabe prüfen, Bericht schreiben und beenden
public partial class MainWindow : Window
{
    private readonly SoundLibrary _library;
    private readonly KeyBindStore _keys;
    private readonly SoundPlayer? _player;
    private readonly string? _playerError;
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _progressTimer;
    private GlobalHotKeys? _hotKeys;
    private List<TileViewModel> _tiles = new();
    private TileViewModel? _recording;
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        if (Arg("--size")?.Split('x') is [var w, var h]) { Width = double.Parse(w); Height = double.Parse(h); }

        _library = new SoundLibrary(Arg("--library"));
        _keys = new KeyBindStore(Path.Combine(Path.GetDirectoryName(_library.Folder)!, "keybinds.json"));

        try { _player = new SoundPlayer(); }
        catch (Exception ex) { _playerError = ex.Message; }

        Volume.Value = Math.Clamp(_settings.Volume, 0, SoundPlayer.MaxVolume);
        Overlap.IsChecked = _settings.Overlap;
        _loading = false;
        ApplyVolume();

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateProgress(), Dispatcher);
        _progressTimer.Stop();

        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible; };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        SourceInitialized += (_, _) =>
        {
            _hotKeys = new GlobalHotKeys(new WindowInteropHelper(this).Handle);
            _hotKeys.Pressed += id => { if (_recording is null && _tiles.FirstOrDefault(t => t.FileName == id) is { } t) Play(t); };
            RegisterHotKeys();
        };
        Loaded += (_, _) =>
        {
            Keyboard.Focus(this); // Tasten sofort nutzbar
            if (_playerError is not null) ShowError("Kein Audiogerät gefunden: " + _playerError);
            RunTestArguments();
        };
        Closing += (_, _) => { _settings.Save(); _hotKeys?.Dispose(); _player?.Dispose(); };

        Render();
    }

    private static string? Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasArg(string name) => Environment.GetCommandLineArgs().Contains(name);

    // ---------- Liste ----------

    private void Render()
    {
        _keys.Sync(_library.Sounds);
        var q = Search.Text.Trim();
        var sounds = string.IsNullOrEmpty(q)
            ? _library.Sounds
            : _library.Sounds.Where(s => s.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        _tiles = sounds.Select(s => new TileViewModel(s)).ToList();
        Tiles.ItemsSource = _tiles;
        UpdateKeyBadges();

        int n = _library.Sounds.Count;
        CountText.Text = n == 1 ? "1 Sound" : $"{n} Sounds";
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = n > 0 && _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Text = $"Keine Treffer für „{q}“";
        Scroller.Visibility = _tiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgress();
    }

    private void UpdateKeyBadges()
    {
        foreach (var t in _tiles)
        {
            t.Bind = _keys.Binds.GetValueOrDefault(t.FileName);
            t.HotKeyState = _hotKeys?.Active.Contains(t.FileName) == true ? "global"
                : _hotKeys?.Failed.Contains(t.FileName) == true ? "failed" : "local";
        }
    }

    private void RegisterHotKeys()
    {
        if (_recording is not null) return;
        _hotKeys?.Register(_keys.Binds);
        UpdateKeyBadges();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    // ---------- Abspielen ----------

    private void Play(TileViewModel tile)
    {
        if (_player is null) { ShowError("Kein Audiogerät gefunden."); return; }
        if (Overlap.IsChecked != true) _player.StopAll();
        try
        {
            _player.Play(tile.Sound);
            tile.IsPlaying = true;
            StopBtn.IsEnabled = true;
            _progressTimer.Start();
        }
        catch (Exception ex)
        {
            ShowError($"„{tile.Title}“ konnte nicht abgespielt werden: {ex.Message}");
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
        foreach (var t in _tiles)
        {
            if (progress.TryGetValue(t.FileName, out var p)) { t.IsPlaying = true; t.Progress = p; }
            else if (t.IsPlaying) { t.IsPlaying = false; t.Progress = 0; }
        }
        StopBtn.IsEnabled = progress.Count > 0;
        if (progress.Count == 0) _progressTimer?.Stop();
    }

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TileViewModel tile } el) return;
        Play(tile);
        // kurzer Drück-Effekt
        if (el.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            scale = new ScaleTransform();
            el.RenderTransform = scale;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        var anim = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(140));
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
        VolumeText.SetResourceReference(TextBlock.ForegroundProperty,
            Volume.Value > 1.0001 ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
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
        RegisterHotKeys();
        if (result.rejected.Count > 0)
        {
            var list = string.Join("\n", result.rejected.Take(5)) + (result.rejected.Count > 5 ? $"\n… und {result.rejected.Count - 5} weitere" : "");
            ShowError($"Diese Dateien sind keine Sounds und wurden übersprungen:\n\n{list}");
        }
    }

    // ---------- Kontextmenü ----------

    private static TileViewModel? TileFrom(object sender) => (sender as FrameworkElement)?.DataContext as TileViewModel;
    private void Menu_Play(object sender, RoutedEventArgs e) { if (TileFrom(sender) is { } t) Play(t); }
    private void Menu_Key(object sender, RoutedEventArgs e) { if (TileFrom(sender) is { } t) StartRecording(t); }
    private void Key_Click(object sender, RoutedEventArgs e) { if (TileFrom(sender) is { } t) StartRecording(t); e.Handled = true; }

    private void Menu_Reveal(object sender, RoutedEventArgs e)
    {
        if (TileFrom(sender) is { } t)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t.Sound.Path}\"") { UseShellExecute = true });
    }

    private void Menu_Delete(object sender, RoutedEventArgs e)
    {
        if (TileFrom(sender) is not { } t) return;
        var answer = MessageBox.Show(this, $"„{t.Title}“ in den Papierkorb verschieben?", "Sound löschen",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        StopAll();
        _keys.Set(t.FileName, null);
        _library.Delete(t.Sound);
        Render();
        RegisterHotKeys();
    }

    // ---------- Taste aufnehmen ----------

    private void StartRecording(TileViewModel tile)
    {
        _hotKeys?.UnregisterAll(); // sonst würde eine schon vergebene Kombi abspielen statt aufnehmen
        _recording = tile;
        RecorderTitle.Text = $"Taste für „{tile.Title}“";
        RecorderNote.Visibility = Visibility.Collapsed;
        UpdateRecorder();
        Recorder.Visibility = Visibility.Visible;
        Keyboard.Focus(this);
    }

    private void UpdateRecorder()
    {
        var bind = _recording is null ? null : _keys.Binds.GetValueOrDefault(_recording.FileName);
        RecorderKey.Text = bind?.Display ?? "–";
        RemoveKeyBtn.IsEnabled = bind is not null;
    }

    private void StopRecording()
    {
        _recording = null;
        Recorder.Visibility = Visibility.Collapsed;
        RegisterHotKeys();
        Keyboard.Focus(this);
    }

    private void Record(Key key, ModifierKeys modifiers)
    {
        if (_recording is null) return;
        if (modifiers.HasFlag(ModifierKeys.Windows)) { Note("Kombinationen mit der Windows-Taste gehören Windows."); return; }
        if (modifiers == ModifierKeys.Control && key is Key.F or Key.O) { Note("Strg+F und Strg+O sind schon vergeben (Suchen, Importieren)."); return; }

        var bind = KeyBind.From(key, modifiers);
        var previous = _keys.Set(_recording.FileName, bind);
        UpdateRecorder();
        if (previous is not null)
        {
            var name = _library.Sounds.FirstOrDefault(s => s.FileName == previous)?.Title ?? previous;
            Note($"{bind.Display} war bei „{name}“ – dort ist sie jetzt entfernt.");
            return; // offen lassen, damit man den Hinweis sieht
        }
        StopRecording();
    }

    private void Note(string text)
    {
        RecorderNote.Text = text;
        RecorderNote.Visibility = Visibility.Visible;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is not null) _keys.Set(_recording.FileName, null);
        StopRecording();
    }

    private void RecorderDone_Click(object sender, RoutedEventArgs e) => StopRecording();
    private void RecorderBackground_MouseDown(object sender, MouseButtonEventArgs e) => StopRecording();
    private void RecorderCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ---------- Tastatur ----------

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Bei Alt-Kombinationen steckt die echte Taste in SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (_recording is not null)
        {
            e.Handled = true;
            if (IsModifier(key)) return;
            if (key == Key.Escape && mods == ModifierKeys.None) StopRecording();
            else if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) RemoveKey_Click(this, e);
            else Record(key, mods);
            return;
        }

        if (mods == ModifierKeys.Control && key == Key.F) { Search.Focus(); Search.SelectAll(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && key == Key.O) { ImportFiles_Click(this, e); e.Handled = true; return; }
        if (key == Key.Escape)
        {
            StopAll();
            if (Search.IsKeyboardFocused) Keyboard.Focus(this);
            e.Handled = true;
            return;
        }
        if (Search.IsKeyboardFocused)
        {
            if (key == Key.Enter && _tiles.Count > 0) { Play(_tiles[0]); e.Handled = true; }
            return;
        }
        if (IsModifier(key)) return;

        var id = _keys.SoundFor(key, mods);
        if (id is not null && _hotKeys?.Active.Contains(id) != true) // globale Kürzel kommen schon über WM_HOTKEY
        {
            var tile = _tiles.FirstOrDefault(t => t.FileName == id) ?? new TileViewModel(_library.Sounds.First(s => s.FileName == id));
            Play(tile);
            e.Handled = true;
        }
    }

    private void ShowError(string text) =>
        MessageBox.Show(this, text, "ClipSound", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- Tests ----------

    private void RunTestArguments()
    {
        var snapshot = Arg("--snapshot");
        var selftest = Arg("--selftest");
        if (snapshot is null && selftest is null && !HasArg("--recorder")) return;

        var steps = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        steps.Tick += (_, _) =>
        {
            steps.Stop();
            if (HasArg("--recorder") && _tiles.Count > 0) StartRecording(_tiles[0]);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (snapshot is not null) SaveSnapshot(snapshot);
                if (selftest is not null) WriteSelftest(selftest);
                if (snapshot is not null || selftest is not null) Close();
            });
        };
        steps.Start();
    }

    private void SaveSnapshot(string path)
    {
        UpdateLayout();
        var root = (FrameworkElement)Content;
        int w = (int)root.ActualWidth, h = (int)root.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Mica ist im Bild durchsichtig – darum mit der Fenster-Grundfarbe hinterlegen
            var bg = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private void WriteSelftest(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sounds: {_library.Sounds.Count}, Kacheln: {_tiles.Count}, Fenster: {ActualWidth}x{ActualHeight}");
        sb.AppendLine($"Audio: {(_player is null ? "kein Gerät – " + _playerError : "ok")}");
        sb.AppendLine("Tasten: " + string.Join(", ", _library.Sounds.Take(6).Select(s => $"{s.Title}={_keys.Binds.GetValueOrDefault(s.FileName)?.Display ?? "–"}")));

        if (_library.Sounds.Count > 0)
        {
            var first = _library.Sounds[0].FileName;
            _keys.Set(first, KeyBind.From(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
            RegisterHotKeys();
            sb.AppendLine($"Strg+Umschalt+K global registriert: {_hotKeys?.Active.Contains(first)} (fehlgeschlagen: {_hotKeys?.Failed.Contains(first)})");
            var reloaded = new KeyBindStore(Path.Combine(Path.GetDirectoryName(_library.Folder)!, "keybinds.json"));
            sb.AppendLine($"Nach Neuladen: {reloaded.Binds.GetValueOrDefault(first)?.Display}");

            // Kachel-Raster: dürfen sich nicht überlappen
            var rects = Enumerable.Range(0, Tiles.Items.Count)
                .Select(i => Tiles.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
                .Where(c => c is not null)
                .Select(c => c!.TransformToAncestor(Tiles).TransformBounds(new Rect(c.RenderSize)))
                .ToList();
            int overlaps = 0;
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                    if (Rect.Intersect(rects[i], rects[j]) is { IsEmpty: false, Width: > 0.5, Height: > 0.5 }) overlaps++;
            sb.AppendLine($"Kacheln geprüft: {rects.Count}, Überlappungen: {overlaps}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}

/// <summary>Lautstärke und Überlappen merken (%AppData%\ClipSound\settings.json).</summary>
public sealed class Settings
{
    public double Volume { get; set; } = 0.8;
    public bool Overlap { get; set; }

    private static string FilePath => Path.Combine(SoundLibrary.SupportFolder, "settings.json");

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
