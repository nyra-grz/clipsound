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
//   --pretend-version <v> so tun, als wäre Version <v> installiert (Updater testen)
//   --update-now         verfügbares Update ohne Nachfrage installieren
//   --selftest <txt>     Tastenkürzel und Wiedergabe prüfen, Bericht schreiben und beenden
//   --hotkey-test <txt>  Alt+2 auf den ersten Sound legen, 15 s auf den globalen Kürzel warten, Ergebnis schreiben
//   --lobby-host         Lobby mit den eigenen Sounds öffnen      --lobby-join <code>  Lobby beitreten
//   --lobby-accept       Anfragen/Angebote annehmen               --lobby-play-first   als Gast ersten Sound drücken, dann behalten wollen
//   --lobby-log <txt>    Ereignisse mitschreiben                  --lobby-exit-after <s> nach s Sekunden beenden
//   Server ändern: Umgebungsvariable CLIPSOUND_SERVER
public partial class MainWindow : Window
{
    private readonly SoundLibrary _library;
    private readonly KeyBindStore _keys;
    private readonly SoundPlayer? _player;
    private readonly string? _playerError;
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _progressTimer;
    private GlobalHotKeys? _hotKeys;
    private readonly Lobby _lobby = new();
    private readonly DispatcherTimer _toastTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    /// <summary>Wirklich beenden (Menü „Beenden“ oder Update) statt in den Infobereich</summary>
    public static bool Quitting { get; set; }
    private List<TileViewModel> _tiles = new();
    private TileViewModel? _recording;
    private bool _loading = true;
    /// <summary>Bei Testläufen keine Meldungsfenster – die würden den Test blockieren.</summary>
    public static bool TestMode { get; } = HasArg("--snapshot") || HasArg("--selftest") || HasArg("--hotkey-test")
        || HasArg("--lobby-host") || HasArg("--lobby-join");
    private readonly List<string> _testErrors = new();

    public MainWindow()
    {
        InitializeComponent();
        if (Arg("--size")?.Split('x') is [var w, var h]) { Width = double.Parse(w); Height = double.Parse(h); }

        _library = new SoundLibrary(Arg("--library"));
        _keys = new KeyBindStore(Path.Combine(_library.SettingsFolder, "keybinds.json"));

        try { _player = new SoundPlayer(); }
        catch (Exception ex) { _playerError = ex.Message; }

        Volume.Value = Math.Clamp(_settings.Volume, 0, SoundPlayer.MaxVolume);
        Overlap.IsChecked = _settings.Overlap;
        _loading = false;
        ApplyVolume();

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateProgress(), Dispatcher);
        _progressTimer.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer!.Stop(); }, Dispatcher);
        _toastTimer.Stop();
        SetUpLobby();

        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible; };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        SourceInitialized += (_, _) =>
        {
            _hotKeys = new GlobalHotKeys(new WindowInteropHelper(this).Handle);
            _hotKeys.Pressed += id =>
            {
                _hotKeyFired = id;
                if (_recording is null && _library.Sounds.FirstOrDefault(s => s.FileName == id) is { } sound) Play(new TileViewModel(sound));
            };
            RegisterHotKeys();
        };
        Loaded += (_, _) =>
        {
            Keyboard.Focus(this); // Tasten sofort nutzbar
            RunTestArguments();
            Updater.CleanupOldVersion();
            if (!TestMode || HasArg("--pretend-version")) _ = CheckForUpdateAsync();
            // Meldung erst danach – ein Meldungsfenster darf den Start (und den Updater) nicht aufhalten
            if (_playerError is not null)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowError("Kein Audiogerät gefunden: " + _playerError));
        };
        Closing += (_, e) =>
        {
            // Fenster zu heißt nicht beenden: die Tastenkürzel sollen weiter überall gehen
            if (!Quitting && !TestMode && _tray is not null)
            {
                e.Cancel = true;
                Hide();
                if (!_settings.TrayHintShown)
                {
                    _tray.ShowBalloonTip(4000, "ClipSound läuft weiter", "Deine Tastenkürzel gehen weiter. Beenden: Rechtsklick auf das Symbol → Beenden.", System.Windows.Forms.ToolTipIcon.Info);
                    _settings.TrayHintShown = true;
                }
                return;
            }
            _settings.LobbyName = LobbyName.Text.Trim();
            _settings.Save();
            if (_lobby.Active) _lobby.Leave();
            _hotKeys?.Dispose(); _player?.Dispose();
            _tray?.Dispose();
        };
        if (!TestMode) SetUpTray();

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

    /// <summary>Gast in einer fremden Lobby: dann zeigt das Fenster die Sounds des Hosts</summary>
    private bool InGuestLobby => _lobby.Active && !_lobby.IsHost;

    private void Render()
    {
        _keys.Sync(_library.Sounds);
        var q = Search.Text.Trim();
        if (InGuestLobby)
        {
            var lobbySounds = _lobby.Sounds.Where(s => q.Length == 0 || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            _tiles = lobbySounds.Select(s => new TileViewModel(s.Local ?? new Sound(s.Name), s)).ToList();
            Tiles.ItemsSource = _tiles;
            int count = _lobby.Sounds.Count;
            CountText.Text = $"Lobby von {_lobby.HostName} · " + (count == 1 ? "1 Sound" : $"{count} Sounds");
            EmptyState.Visibility = Visibility.Collapsed;
            NoResults.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResultsText.Text = count == 0
                ? (_lobby.Phase == LobbyPhase.Open ? $"{_lobby.HostName} hat noch keine Sounds freigegeben." : "Verbinde …")
                : $"Keine Treffer für „{q}“";
            Scroller.Visibility = _tiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateProgress();
            return;
        }
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
        // Lobby: läuft über den Server, damit es bei allen gleichzeitig startet
        if (tile.Lobby is not null) { _lobby.PlayGuest(tile.Lobby); return; }
        if (_lobby.Active && _lobby.IsHost)
        {
            if (_lobby.PlayOwn(tile.Sound)) return;
            ShowToast($"„{tile.Title}“ ist noch nicht in der Lobby – nur bei dir abgespielt.");
        }
        PlayLocal(tile.Sound, TimeSpan.Zero, tile.Title);
    }

    private void PlayLocal(Sound sound, TimeSpan delay, string title)
    {
        LobbyLog($"play {sound.FileName} in {(int)delay.TotalMilliseconds} ms");
        if (_player is null) { ShowError("Kein Audiogerät gefunden."); return; }
        if (Overlap.IsChecked != true) _player.StopAll();
        try
        {
            _player.Play(sound, delay);
            StopBtn.IsEnabled = true;
            _progressTimer.Start();
        }
        catch (Exception ex)
        {
            ShowError($"„{title}“ konnte nicht abgespielt werden: {ex.Message}");
        }
    }

    private void StopAll()
    {
        _player?.StopAll();
        if (_lobby.Active) _lobby.StopAll();
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
        StopBtn.IsEnabled = progress.Count > 0 || _lobby.Active;
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

    /// <summary>Kontextmenü je nach Lage: eigener Sound, als Host in der Lobby oder Sound des Hosts</summary>
    private void TileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is not TileViewModel tile) return;
        MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            return mi;
        }
        menu.Items.Add(Item("Abspielen", () => Play(tile)));
        if (tile.Lobby is { } ls)
        {
            menu.Items.Add(Item("Behalten …", () => _lobby.Ask(ls)));
            return;
        }
        menu.Items.Add(Item("Taste festlegen …", () => StartRecording(tile)));
        menu.Items.Add(Item("Im Explorer zeigen", () =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{tile.Sound.Path}\"") { UseShellExecute = true })));
        if (_lobby.IsHost && _lobby.Guests.Any())
        {
            var gift = new MenuItem { Header = "Schenken an" };
            foreach (var m in _lobby.Guests) gift.Items.Add(Item(m.Name, () => _lobby.Offer(tile.Sound, m)));
            menu.Items.Add(gift);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Löschen …", () => Menu_Delete(tile)));
    }
    private void Key_Click(object sender, RoutedEventArgs e) { if (TileFrom(sender) is { Lobby: null } t) StartRecording(t); e.Handled = true; }

    private void Menu_Delete(TileViewModel t)
    {
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
        if (tile.Lobby is not null) return; // Sounds des Hosts haben keine Tasten
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
        if ((bind.Mods & (KeyBind.Ctrl | KeyBind.Alt)) == (KeyBind.Ctrl | KeyBind.Alt) && (bind.Mods & KeyBind.Shift) == 0)
            Note("Strg+Alt ist AltGr (für @, € …) – geht nur im ClipSound-Fenster. Nimm Alt allein, z. B. Alt+2.");
        var previous = _keys.Set(_recording.FileName, bind);
        UpdateRecorder();
        if (previous is not null)
        {
            var name = _library.Sounds.FirstOrDefault(s => s.FileName == previous)?.Title ?? previous;
            Note($"{bind.Display} war bei „{name}“ – dort ist sie jetzt entfernt.");
            return; // offen lassen, damit man den Hinweis sieht
        }
        if (RecorderNote.Visibility == Visibility.Visible) return; // Hinweis von oben stehen lassen
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
        if (Keyboard.FocusedElement is TextBox) return; // z. B. Lobby-Code: Zahlen sollen ins Feld, nicht Sounds starten
        if (IsModifier(key)) return;

        var id = _keys.SoundFor(key, mods);
        if (id is not null && _hotKeys?.Active.Contains(id) != true) // globale Kürzel kommen schon über WM_HOTKEY
        {
            var tile = _tiles.FirstOrDefault(t => t.FileName == id) ?? new TileViewModel(_library.Sounds.First(s => s.FileName == id));
            Play(tile);
            e.Handled = true;
        }
    }

    // ---------- Lobby ----------

    private string? _hotKeyFired;
    private bool _playedFirst;

    private void SetUpLobby()
    {
        LobbyName.Text = _settings.LobbyName ?? "";
        _lobby.GetSystemVolume = SystemVolume.Get;
        _lobby.SetSystemVolume = SystemVolume.Set;
        _lobby.Changed += () => { Render(); UpdateLobbyUi(); UpdateVolumeRows(); LobbyTestStep(); };
        _lobby.Note += ShowToast;
        _lobby.Ended += text => { LobbyLog("ende: " + text); LobbyError.Text = text; LobbyError.Visibility = Visibility.Visible; if (LobbyPanel.Visibility != Visibility.Visible) ShowError(text); Render(); UpdateLobbyUi(); };
        _lobby.Play += (sound, delay) => PlayLocal(sound, delay, Path.GetFileNameWithoutExtension(sound.FileName));
        _lobby.Stop += () => { _player?.StopAll(); UpdateProgress(); };
        _lobby.Receive += path =>
        {
            LobbyLog("bekommen " + Path.GetFileName(path));
            Import(new[] { path });
        };
        _lobby.Request += request =>
        {
            LobbyLog($"anfrage {(request.Asked ? "asked" : "offered")} {request.SoundName} von {request.By}");
            bool ok;
            if (TestMode) ok = HasArg("--lobby-accept");
            else
            {
                var text = request.Asked
                    ? $"{request.By} möchte „{request.SoundTitle}“ behalten.\n\nDer Sound wird bei {request.By} gespeichert."
                    : $"{request.By} schenkt dir „{request.SoundTitle}“.\n\nDer Sound kommt in deine Sounds.";
                ok = MessageBox.Show(this, text, "Lobby", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            }
            _lobby.Answer(request, ok);
        };
        UpdateLobbyUi();
    }

    private void UpdateLobbyUi()
    {
        bool active = _lobby.Active;
        LobbyBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LobbyStart.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        LobbyInside.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ImportBtn.IsEnabled = !InGuestLobby;
        int n = _library.Sounds.Count;
        LobbyOpenHint.Text = n == 0 ? "Du hast noch keine Sounds zum Teilen." : $"Deine {n} Sounds werden für die anderen freigegeben.";
        LobbyOpenBtn.IsEnabled = LobbyJoinBtn.IsEnabled = !active;
        if (!active) return;

        var title = _lobby.IsHost ? "Deine Lobby" : $"Lobby von {_lobby.HostName}";
        LobbyBarTitle.Text = title;
        LobbyBarCode.Text = LobbyBigCode.Text = _lobby.Code;
        LobbyInsideTitle.Text = _lobby.IsHost ? "Dein Lobby-Code" : title;
        var others = _lobby.Members.Where(m => m.Id != _lobby.Me).Select(m => m.Name).ToList();
        string status = _lobby.Phase switch
        {
            LobbyPhase.Connecting => "Verbinde …",
            LobbyPhase.Reconnecting => "Verbindung weg – verbinde neu …",
            _ => others.Count == 0 ? "Noch niemand da – schick den Code rum" : "Mit " + string.Join(", ", others),
        };
        if (_lobby.IsHost && _lobby.Uploaded is { } u && u.Done < u.Total) status += $" · Sounds werden freigegeben: {u.Done} von {u.Total}";
        LobbyBarStatus.Text = status;
        LobbyMembersText.Text = string.Join("\n", _lobby.Members.Select(m => (m.Host ? "👑 " : "• ") + m.Name + (m.Id == _lobby.Me ? " (du)" : "")));
        LobbyHostHint.Visibility = _lobby.IsHost ? Visibility.Visible : Visibility.Collapsed;
        // Gast: sobald drin, Fenster zu – die Sounds sieht man im Hauptfenster
        if (!_lobby.IsHost && _lobby.Phase == LobbyPhase.Open && LobbyPanel.Visibility == Visibility.Visible && _closeLobbyPanelOnOpen)
        {
            _closeLobbyPanelOnOpen = false;
            LobbyPanel.Visibility = Visibility.Collapsed;
        }
    }
    private bool _closeLobbyPanelOnOpen;

    private string LobbyPersonName => LobbyName.Text.Trim() is { Length: > 0 } n ? n : Environment.UserName;

    private void Lobby_Click(object sender, RoutedEventArgs e)
    {
        LobbyError.Visibility = Visibility.Collapsed;
        UpdateLobbyUi();
        LobbyPanel.Visibility = Visibility.Visible;
        if (!_lobby.Active) LobbyCode.Focus();
    }

    private void LobbyOpen_Click(object sender, RoutedEventArgs e)
    {
        LobbyError.Visibility = Visibility.Collapsed;
        _settings.LobbyName = LobbyName.Text.Trim();
        _ = _lobby.OpenAsync(LobbyPersonName, _library.Sounds);
        UpdateLobbyUi();
    }

    private void LobbyJoin_Click(object sender, RoutedEventArgs e)
    {
        LobbyError.Visibility = Visibility.Collapsed;
        _settings.LobbyName = LobbyName.Text.Trim();
        _closeLobbyPanelOnOpen = true;
        _lobby.Join(LobbyCode.Text, LobbyPersonName);
        UpdateLobbyUi();
    }

    private void LobbyCode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { LobbyJoin_Click(sender, e); e.Handled = true; }
    }

    private void LobbyCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_lobby.Code); ShowToast($"Code {_lobby.Code} kopiert"); } catch { }
    }

    private void LobbyLeave_Click(object sender, RoutedEventArgs e)
    {
        _lobby.Leave();
        Render();
        UpdateLobbyUi();
    }

    // ---------- Lautstärke der Geräte (versteckt) ----------

    private void LobbyCode_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3) return;
        _volumeRowIds = "";
        UpdateVolumeRows();
        VolumePopup.PlacementTarget = (UIElement)sender;
        VolumePopup.IsOpen = true;
        e.Handled = true;
    }

    private string _volumeRowIds = "";
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _volumeRows = new();

    /// <summary>Zeilen nur neu bauen, wenn sich die Leute ändern – sonst springt der Regler beim Ziehen</summary>
    private void UpdateVolumeRows()
    {
        if (!_lobby.Active) { VolumePopup.IsOpen = false; return; }
        var ids = string.Join(",", _lobby.Members.Select(m => m.Id + (m.Volume is null ? "-" : "+")));
        if (ids != _volumeRowIds)
        {
            _volumeRowIds = ids;
            VolumeRows.Children.Clear();
            _volumeRows.Clear();
            foreach (var m in _lobby.Members)
            {
                bool me = m.Id == _lobby.Me;
                var row = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                var head = new Grid();
                head.Children.Add(new TextBlock { Text = (m.Host ? "👑 " : "") + m.Name + (me ? " (du)" : "") });
                var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                head.Children.Add(value);
                row.Children.Add(head);
                if (m.Volume is null)
                {
                    value.Text = "–";
                    var none = new TextBlock { Text = "Dieses Gerät meldet keine Lautstärke.", FontSize = 12 };
                    none.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    row.Children.Add(none);
                }
                else
                {
                    var slider = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.02, LargeChange = 0.1, IsMoveToPointEnabled = true,
                                              Value = m.Volume.Value };
                    var member = m;
                    slider.ValueChanged += (_, e) =>
                    {
                        value.Text = $"{Math.Round(e.NewValue * 100)} %";
                        if (!slider.IsMouseCaptureWithin && !slider.IsKeyboardFocusWithin) return; // nur echte Bedienung
                        if (me) _lobby.SetOwnVolume(e.NewValue); else _lobby.SetVolume(member, e.NewValue);
                    };
                    value.Text = $"{Math.Round(m.Volume.Value * 100)} %";
                    row.Children.Add(slider);
                    if (m.System == false)
                    {
                        var app = new TextBlock { Text = "Nur die App – im Browser geht es nicht anders.", FontSize = 11 };
                        app.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                        row.Children.Add(app);
                    }
                    _volumeRows[m.Id] = (slider, value);
                }
                VolumeRows.Children.Add(row);
            }
            VolumeHint.Text = "Stellt die echten Lautsprecher ein.";
            return;
        }
        foreach (var m in _lobby.Members)
        {
            if (m.Volume is not { } v || !_volumeRows.TryGetValue(m.Id, out var r) || r.Slider.IsMouseCaptureWithin) continue;
            r.Slider.Value = v;
        }
    }

    private void LobbyClose_Click(object sender, RoutedEventArgs e) { LobbyPanel.Visibility = Visibility.Collapsed; Keyboard.Focus(this); }
    private void LobbyBackground_MouseDown(object sender, MouseButtonEventArgs e) => LobbyClose_Click(sender, e);

    private void ShowToast(string text)
    {
        LobbyLog("note: " + text);
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- Infobereich ----------

    private void SetUpTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "ClipSound",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Visible = true,
                ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
            };
            _tray.ContextMenuStrip.Items.Add("ClipSound öffnen", null, (_, _) => ShowFromTray());
            _tray.ContextMenuStrip.Items.Add("Beenden", null, (_, _) => { Quitting = true; Close(); });
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch { _tray = null; } // ohne Symbol wird beim Schließen einfach beendet
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowError(string text)
    {
        if (TestMode) { _testErrors.Add(text); return; }
        MessageBox.Show(this, text, "ClipSound", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- Updates ----------

    private Updater.Release? _update;

    private async Task CheckForUpdateAsync()
    {
        try { _update = await Updater.CheckAsync(); }
        catch { return; } // offline – egal, beim nächsten Start wieder
        if (_update is null) return;
        UpdateTitle.Text = $"ClipSound {_update.Version} ist verfügbar";
        UpdateSubtitle.Text = $"Du hast {Updater.CurrentVersion}.";
        UpdateBar.Visibility = Visibility.Visible;
        if (HasArg("--update-now")) UpdateNow_Click(this, new RoutedEventArgs());
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        UpdateButtons.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateSubtitle.Text = "Wird heruntergeladen …";
        try
        {
            await Updater.InstallAsync(_update, new Progress<double>(p => UpdateProgressBar.Value = p));
        }
        catch (Exception ex)
        {
            UpdateButtons.Visibility = Visibility.Visible;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateSubtitle.Text = $"Du hast {Updater.CurrentVersion}.";
            ShowError("Update fehlgeschlagen: " + ex.Message);
        }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    private void UpdateNotes_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_update?.Page ?? $"https://github.com/{Updater.Repo}/releases/latest") { UseShellExecute = true });

    // ---------- Tests ----------

    private string _lastLobbyState = "";

    private void LobbyLog(string line)
    {
        if (Arg("--lobby-log") is not { } file) return;
        try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>Testläufe: Zustand mitschreiben, als Gast ersten Sound drücken und behalten wollen</summary>
    private void LobbyTestStep()
    {
        int ready = _lobby.Sounds.Count(x => x.Local is not null);
        var state = $"phase={_lobby.Phase} code={_lobby.Code} host={_lobby.IsHost} sounds={ready}/{_lobby.Sounds.Count} " +
                    $"hochgeladen={(_lobby.Uploaded is { } u ? $"{u.Done}/{u.Total}" : "-")} leute=[{string.Join(", ", _lobby.Members.Select(m => m.Name))}]";
        if (state != _lastLobbyState) { _lastLobbyState = state; LobbyLog(state); }
        if (HasArg("--lobby-play-first") && !_playedFirst && !_lobby.IsHost && _lobby.Phase == LobbyPhase.Open
            && _lobby.Sounds.Count > 0 && ready == _lobby.Sounds.Count)
        {
            _playedFirst = true;
            var first = _lobby.Sounds[0];
            _lobby.PlayGuest(first);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (_, _) => { t.Stop(); _lobby.Ask(first); };
            t.Start();
        }
    }

    private void RunLobbyAndHotkeyTests()
    {
        if (HasArg("--lobby-host")) _ = _lobby.OpenAsync(LobbyPersonName, _library.Sounds);
        if (Arg("--lobby-join") is { } code) _lobby.Join(code, LobbyPersonName);
        if (Arg("--lobby-exit-after") is { } secs)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(double.Parse(secs, System.Globalization.CultureInfo.InvariantCulture)) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                LobbyLog($"ende bibliothek={_library.Sounds.Count} [{string.Join(", ", _library.Sounds.Select(x => x.FileName))}]");
                Quitting = true;
                Close();
            };
            t.Start();
        }
        if (Arg("--hotkey-test") is { } file && _library.Sounds.Count > 0)
        {
            var first = _library.Sounds[0].FileName;
            _keys.Set(first, KeyBind.From(Key.D2, ModifierKeys.Alt));
            RegisterHotKeys();
            var info = $"Alt+2 global: {_keys.Binds[first].IsGlobal}, registriert: {_hotKeys?.Active.Contains(first)}, fehlgeschlagen: {_hotKeys?.Failed.Contains(first)}";
            File.WriteAllText(file, info + Environment.NewLine + "warte auf Tastendruck …" + Environment.NewLine);
            var started = DateTime.Now;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) =>
            {
                if (_hotKeyFired is null && DateTime.Now - started < TimeSpan.FromSeconds(15)) return;
                t.Stop();
                File.AppendAllText(file, (_hotKeyFired is null ? "NICHT ausgelöst" : $"ausgelöst für {_hotKeyFired}") + Environment.NewLine);
                Close();
            };
            t.Start();
        }
    }

    private void RunTestArguments()
    {
        RunLobbyAndHotkeyTests();
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
        if (_testErrors.Count > 0) sb.AppendLine("Meldungen: " + string.Join(" | ", _testErrors));
        sb.AppendLine("Tasten: " + string.Join(", ", _library.Sounds.Take(6).Select(s => $"{s.Title}={_keys.Binds.GetValueOrDefault(s.FileName)?.Display ?? "–"}")));

        // Import testen: --import-test <ordner> importiert echte Dateien (z. B. MP3s) und schreibt das Ergebnis
        if (Arg("--import-test") is { } importDir)
        {
            var (added, rejected) = _library.Import(new[] { importDir });
            sb.AppendLine($"Import: {added} übernommen, {rejected.Count} abgelehnt");
            foreach (var r in rejected) sb.AppendLine("  abgelehnt: " + r);
            foreach (var f in Directory.GetFiles(importDir))
            {
                string standard;
                try { using var r = new NAudio.Wave.AudioFileReader(f); standard = r.Read(new float[4096], 0, 4096) > 0 ? "ok" : "leer"; }
                catch (Exception ex) { standard = "FEHLER " + ex.GetType().Name + ": " + ex.Message; }
                sb.AppendLine($"  {Path.GetFileName(f)}: Windows-Decoder {standard} | ClipSound: {SoundPlayer.CheckDecodable(f) ?? "dekodiert ok"}");
            }
            Render();
        }

        if (_library.Sounds.Count > 0)
        {
            var first = _library.Sounds[0].FileName;
            _keys.Set(first, KeyBind.From(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
            RegisterHotKeys();
            sb.AppendLine($"Strg+Umschalt+K global registriert (soll True): {_hotKeys?.Active.Contains(first)} (fehlgeschlagen: {_hotKeys?.Failed.Contains(first)})");
            var reloaded = new KeyBindStore(Path.Combine(_library.SettingsFolder, "keybinds.json"));
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
    public string? LobbyName { get; set; }
    public bool TrayHintShown { get; set; }

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
