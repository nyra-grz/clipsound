using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
//   --page <seite>       sounds | lobby | account | settings | clan (erster Clan)
//   --theme dark|light   hell/dunkel statt der Windows-Einstellung
//   --recorder           Aufnahmefenster für den ersten Sound öffnen
//   --play-first         ersten Sound abspielen (für Bilder der Wiedergabeleiste)
//   --snapshot <png>     Fenster als Bild speichern und beenden    --snapshot-delay <s>  vorher warten (Standard 1,5)
//   --settings           Einstellungen öffnen (wie --page settings)
//   --icon <bild>        eigenes App-Icon setzen (wie in den Einstellungen)
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
    private readonly Account _account;
    private readonly DispatcherTimer _toastTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    /// <summary>Wirklich beenden (Menü „Beenden“ oder Update) statt in den Infobereich</summary>
    public static bool Quitting { get; set; }
    private List<TileViewModel> _tiles = new();
    private string _tilesKey = "";   // welche Kacheln gerade stehen – nur neu bauen, wenn sich das ändert
    private List<TileViewModel> _lobbyTiles = new();
    private string _lobbyTilesKey = "";
    private TileViewModel? _recording;
    private bool _loading = true;
    /// <summary>Bei Testläufen keine Meldungsfenster – die würden den Test blockieren.</summary>
    public static bool TestMode { get; } = HasArg("--snapshot") || HasArg("--selftest") || HasArg("--hotkey-test")
        || HasArg("--lobby-host") || HasArg("--lobby-join");
    private readonly List<string> _testErrors = new();

    /// <summary>Seiten in der Navigationsleiste (wie am Mac)</summary>
    private enum Section { Sounds, Lobby, Account, Clan, Settings }
    private Section _section = Section.Sounds;
    private string? _clanId;
    private AccountPage? _accountPage;
    private ClanPage? _clanPage;
    /// <summary>--page clan: ersten Clan zeigen, sobald die Clans geladen sind</summary>
    private bool _wantFirstClan;

    public MainWindow()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };
        if (Arg("--size")?.Split('x') is [var w, var h]) { Width = double.Parse(w); Height = double.Parse(h); }

        _library = new SoundLibrary(Arg("--library"));
        _keys = new KeyBindStore(Path.Combine(_library.SettingsFolder, "keybinds.json"));
        _account = new Account(Path.Combine(_library.SettingsFolder, "account.token"));
        _account.Attach(_library); // Clan-Sounds von diesem PC aus weitergeben
        _account.Changed += () => Dispatcher.BeginInvoke(AccountChanged);
        _library.Changed += () =>
        {
            _lobby.HostLibraryChanged(_library.Sounds); // als Host: Liste in der Lobby aktuell halten
            // Sounds kommen auch von der Clan-Seite dazu („In meine Sounds“) – dann hier zeigen
            Dispatcher.BeginInvoke(() => { Render(); RegisterHotKeys(); });
        };

        try
        {
            _player = new SoundPlayer();
            if (!_player.HasOutput) _playerError = _player.OutputError ?? "unbekannt"; // kommt eins dazu, geht es von selbst
        }
        catch (Exception ex) { _playerError = ex.Message; }

        Volume.Value = Math.Clamp(_settings.Volume, 0, SoundPlayer.MaxVolume);
        Overlap.IsChecked = _settings.Overlap;
        _loading = false;
        ApplyVolume();
        ApplyTileSize(_settings.TileSize, save: false);

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateProgress(), Dispatcher);
        _progressTimer.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer!.Stop(); }, Dispatcher);
        _toastTimer.Stop();

        // Symbole der Seiten (farbige Quadrate wie am Mac)
        EmptyIcon.Content = Ui.IconTile("", Palette.Blue, 72);
        LobbyHeroIcon.Content = Ui.IconTile("", Palette.Indigo, 80);
        OpenCardIcon.Content = Ui.IconTile("", Palette.Orange, 40);
        JoinCardIcon.Content = Ui.IconTile("", Palette.Blue, 40);
        LobbyHeadIcon.Content = Ui.IconTile("", Palette.Indigo, 52);
        SetUpLobby();
        SetUpSettingsPage();
        AccountChanged();

        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        SizeChanged += (_, _) => { LayoutPane(); LayoutContent(); };
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
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowError(
                    $"Kein Audiogerät gefunden ({_playerError}). Sobald Lautsprecher oder Kopfhörer da sind, spielt ClipSound von selbst darauf."));
        };
        Closing += (_, e) =>
        {
            // Fenster zu heißt nicht beenden: die Tastenkürzel sollen weiter überall gehen
            if (!Quitting && !TestMode && _tray is not null && _settings.KeepInTray)
            {
                e.Cancel = true;
                Hide();
                if (!_settings.TrayHintShown)
                {
                    _tray.ShowBalloonTip(4000, "ClipSound läuft weiter", "Deine Tastenkürzel gehen weiter. Beenden: Rechtsklick auf das Symbol → Beenden.", System.Windows.Forms.ToolTipIcon.Info);
                    _settings.TrayHintShown = true;
                }
                _settings.LobbyName = LobbyName.Text.Trim();
                _settings.Save(); // falls Windows später einfach heruntergefahren wird
                return;
            }
            _settings.LobbyName = LobbyName.Text.Trim();
            _settings.Save();
            if (_lobby.Active) _lobby.Leave();
            _hotKeys?.Dispose(); _player?.Dispose();
            _tray?.Dispose();
        };
        if (!TestMode) SetUpTray();
        if (Arg("--icon") is { } icon) CustomIcon.Set(icon);
        ApplyIcon();
        // Herunterfahren/Abmelden: richtig beenden (Einstellungen speichern, Symbol weg) statt nur verstecken
        Application.Current.SessionEnding += (_, _) => { Quitting = true; Close(); };
        // Nach dem Ruhezustand ist die Clan-Verbindung oft tot: gleich neu verbinden
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) _account.Share.ReconnectNow(); };

        var start = HasArg("--settings") ? "settings" : Arg("--page");
        _wantFirstClan = start == "clan";
        Navigate(start switch
        {
            "lobby" => Section.Lobby,
            "account" or "clan" => Section.Account,
            "settings" => Section.Settings,
            _ => Section.Sounds,
        });
    }

    /// <summary>Einstellungen kurz nach einer Änderung speichern (nicht erst beim Beenden)</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private void SaveSettingsSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    private static string? Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasArg(string name) => Environment.GetCommandLineArgs().Contains(name);

    // ---------- Navigation ----------

    private bool _navigating;

    private void Navigate(Section section, string? clanId = null)
    {
        if (section == Section.Clan && _account.Clans.All(c => c.Id != clanId)) section = Section.Account;
        _section = section;
        _clanId = section == Section.Clan ? clanId : null;

        _navigating = true;
        NavSounds.IsChecked = section == Section.Sounds;
        NavLobby.IsChecked = section == Section.Lobby;
        NavAccount.IsChecked = section == Section.Account;
        NavSettings.IsChecked = section == Section.Settings;
        foreach (var item in ClanNav.Children.OfType<RadioButton>()) item.IsChecked = _clanId is not null && (string)item.Tag == _clanId;
        _navigating = false;

        SoundsPage.Visibility = section == Section.Sounds ? Visibility.Visible : Visibility.Collapsed;
        LobbyPage.Visibility = section == Section.Lobby ? Visibility.Visible : Visibility.Collapsed;
        AccountHost.Visibility = section == Section.Account ? Visibility.Visible : Visibility.Collapsed;
        ClanHost.Visibility = section == Section.Clan ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == Section.Settings ? Visibility.Visible : Visibility.Collapsed;

        if (section == Section.Account)
        {
            _accountPage ??= new AccountPage(_account, this);
            AccountHost.Content = _accountPage;
            _accountPage.Refresh();
            _ = _account.RefreshMeAsync(); // Clans frisch laden (z. B. inzwischen freigegeben)
        }
        if (section == Section.Clan)
        {
            if (_clanPage?.ClanId != _clanId)
            {
                _clanPage?.Detach();
                var clan = _account.Clans.First(c => c.Id == _clanId);
                _clanPage = new ClanPage(new ClanRoom(_account, _lobby, _library, clan.Id, clan.Name), _lobby, this);
                ClanHost.Content = _clanPage;
            }
        }
        else if (_clanPage is not null) { _clanPage.Detach(); _clanPage = null; ClanHost.Content = null; }
        if (section == Section.Settings) UpdateSettingsPage();

        SearchPlaceholder.Text = section == Section.Lobby && InGuestLobby ? "Lobby durchsuchen" : "Sounds suchen";
        Render();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navigating) return;
        if (sender == NavSounds) Navigate(Section.Sounds);
        else if (sender == NavLobby) Navigate(Section.Lobby);
        else if (sender == NavAccount) Navigate(Section.Account);
        else if (sender == NavSettings) Navigate(Section.Settings);
        else if (sender is RadioButton { Tag: string id }) Navigate(Section.Clan, id);
        Keyboard.Focus(this);
    }

    private void AddClan_Click(object sender, RoutedEventArgs e) => Navigate(Section.Account);

    /// <summary>Von der Konto-Seite aus einen Clan öffnen</summary>
    public void OpenClan(string id) => Navigate(Section.Clan, id);

    /// <summary>Clan aufgelöst, verlassen oder rausgeworfen</summary>
    public void ClanGone()
    {
        _ = _account.RefreshMeAsync();
        Navigate(Section.Account);
    }

    /// <summary>Konto oder Clan-Liste geändert: unten in der Leiste und bei den Clans nachziehen</summary>
    private void AccountChanged()
    {
        var user = _account.User;
        AccountAvatarHost.Content = user is null ? Ui.EmptyAvatar(32) : Ui.Avatar(user.Name, 32);
        AccountName.Text = user?.Name ?? "Anmelden";
        AccountSub.Text = user is null ? "Für Clans mit Freunden" : "Konto & Clans";
        AddClanText.Text = user is null ? "Anmelden für Clans …" : "Clan hinzufügen …";

        ClanNav.Children.Clear();
        foreach (var c in _account.Clans)
        {
            var icon = Ui.ClanIcon(c.Name, c.Id, 20);
            icon.Margin = new Thickness(0, 0, 14, 0);
            var name = new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "wide" };
            Grid.SetColumn(name, 1);
            // Auto-Spalte fürs Zeichen: in der schmalen Leiste abgeschnitten statt gestaucht
            var content = new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() }, Children = { icon, name } };
            var item = new RadioButton { Tag = c.Id, ToolTip = c.Name, Content = content };
            item.SetResourceReference(StyleProperty, "NavItem");
            item.IsChecked = _section == Section.Clan && _clanId == c.Id;
            item.Checked += Nav_Checked;
            ClanNav.Children.Add(item);
        }
        ApplyPaneLabels();

        if (_section == Section.Clan && (user is null || _account.Clans.All(c => c.Id != _clanId))) Navigate(Section.Account);
        else if (_section == Section.Account) _accountPage?.Refresh();
        if (_wantFirstClan && _account.Clans.Count > 0) { _wantFirstClan = false; Navigate(Section.Clan, _account.Clans[0].Id); }
    }

    // ---------- Navigationsleiste: breit oder nur Symbole ----------

    /// <summary>Vom Hamburger-Knopf gewählt (sonst nach Fensterbreite)</summary>
    private bool? _paneOpen;
    private bool _paneCompact;

    private void LayoutPane()
    {
        bool compact = _paneOpen is bool open ? !open : ActualWidth < 900;
        if (compact == _paneCompact) return;
        _paneCompact = compact;
        Pane.Width = compact ? 48 : 280;
        SearchIconBtn.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ApplyPaneLabels();
    }

    /// <summary>Texte, Zahlen und Überschriften (Tag="wide") nur in der breiten Leiste; schmal steht das Symbol mittig</summary>
    private void ApplyPaneLabels()
    {
        foreach (var item in ClanNav.Children.OfType<RadioButton>().Append(NavSounds).Append(NavLobby).Append(NavSettings))
            item.Padding = _paneCompact ? new Thickness(10, 0, 0, 0) : new Thickness(14, 0, 12, 0);
        NavAccount.Padding = _paneCompact ? new Thickness(4, 0, 0, 0) : new Thickness(10, 0, 12, 0);
        AddClanBtn.Padding = _paneCompact ? new Thickness(10, 0, 0, 0) : new Thickness(14, 0, 12, 0);
        void Walk(object node)
        {
            if (node is FrameworkElement { Tag: "wide" } el) el.Visibility = _paneCompact ? Visibility.Collapsed : Visibility.Visible;
            if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(Pane);
        // Abzeichen haben eigene Regeln (Lobby nur, wenn aktiv)
        if (!_paneCompact) LobbyBadge.Visibility = _lobby.Active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Hamburger_Click(object sender, RoutedEventArgs e)
    {
        _paneOpen = _paneCompact;
        LayoutPane();
    }

    private void SearchIcon_Click(object sender, RoutedEventArgs e) => FocusSearch();

    private void FocusSearch()
    {
        if (_paneCompact) { _paneOpen = true; LayoutPane(); }
        Search.Focus();
        Search.SelectAll();
    }

    /// <summary>Wenn es eng wird: Knöpfe nur mit Symbol, Wiedergabeleiste schmaler</summary>
    private void LayoutContent()
    {
        double w = ContentLayer.ActualWidth;
        if (w <= 0) return;
        var labels = w < 620 ? Visibility.Collapsed : Visibility.Visible;
        ImportLabel.Visibility = ViewLabel.Visibility = labels;
        NowPlayingText.Width = w >= 820 ? 160 : w >= 660 ? 120 : 84;
        Volume.Width = w >= 820 ? 120 : w >= 660 ? 90 : 64;
    }

    // ---------- Liste ----------

    /// <summary>Gast in einer fremden Lobby: dann zeigt die Lobby-Seite die Sounds des Hosts</summary>
    private bool InGuestLobby => _lobby.Active && !_lobby.IsHost;

    private void Render()
    {
        _keys.Sync(_library.Sounds);
        var q = Search.Text.Trim();
        var sounds = string.IsNullOrEmpty(q)
            ? _library.Sounds
            : _library.Sounds.Where(s => s.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var ownKey = "own|" + string.Join("|", sounds.Select(s => s.Path));
        if (ownKey != _tilesKey)
        {
            _tilesKey = ownKey;
            _tiles = sounds.Select(s => new TileViewModel(s)).ToList();
            Tiles.ItemsSource = _tiles;
        }
        UpdateKeyBadges();

        int n = _library.Sounds.Count;
        SoundsSubtitle.Text = n == 1 ? "1 Sound" : $"{n} Sounds";
        SoundsBadgeText.Text = n.ToString();
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = n > 0 && _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Text = $"Keine Treffer für „{q}“";
        Scroller.Visibility = _tiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderLobbyTiles(q);
        UpdateProgress();
    }

    /// <summary>Gast: Sounds des Hosts. In der Lobby kommt ständig etwas (Leute, Lautstärke) – Kacheln nur neu bauen,
    /// wenn sich die Sounds ändern, sonst gehen offene Rechtsklick-Menüs zu.</summary>
    private void RenderLobbyTiles(string q)
    {
        var lobbySounds = InGuestLobby
            ? _lobby.Sounds.Where(s => q.Length == 0 || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<LobbySound>();
        var key = "lobby|" + string.Join("|", lobbySounds.Select(s => s.Id + (s.Local is null ? "-" : "+")));
        if (key != _lobbyTilesKey)
        {
            _lobbyTilesKey = key;
            _lobbyTiles = lobbySounds.Select(s => new TileViewModel(s.Local ?? new Sound(s.Name), s)).ToList();
            LobbyTiles.ItemsSource = _lobbyTiles;
        }
        if (!InGuestLobby) return;
        int count = _lobby.Sounds.Count;
        GuestSoundsTitle.Text = _lobby.HostName.Length == 0 ? "Sounds" : $"Sounds von {_lobby.HostName}";
        GuestSoundsCount.Text = count.ToString();
        GuestEmpty.Visibility = _lobbyTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GuestEmpty.Text = count == 0
            ? (_lobby.Phase == LobbyPhase.Open ? $"{_lobby.HostName} hat noch keine Sounds freigegeben." : "Verbinde …")
            : $"Keine Treffer für „{q}“";
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
        // Suchen auf einer Seite ohne Sounds: zu den Sounds (wie am Mac)
        if (Search.Text.Length > 0 && _section != Section.Sounds && !(_section == Section.Lobby && InGuestLobby)) Navigate(Section.Sounds);
        else Render();
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
        if (_player is null) { ShowError("Audio konnte nicht gestartet werden: " + _playerError); return; }
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
        foreach (var t in _tiles.Concat(_lobbyTiles))
        {
            if (progress.TryGetValue(t.FileName, out var p)) { t.IsPlaying = true; t.Progress = p; }
            else if (t.IsPlaying) { t.IsPlaying = false; t.Progress = 0; }
        }
        StopBtn.IsEnabled = progress.Count > 0 || _lobby.Active;
        UpdateNowPlaying(progress);
        if (progress.Count == 0) _progressTimer?.Stop();
    }

    private string? _nowPlayingId;

    /// <summary>Wiedergabeleiste: der zuletzt gestartete Sound mit Fortschritt, „+2“ für weitere</summary>
    private void UpdateNowPlaying(Dictionary<string, double> progress)
    {
        bool playing = progress.Count > 0;
        NowPlaying.Visibility = NowPlayingDivider.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        if (!playing) { _nowPlayingId = null; return; }
        var (id, p) = progress.Last();
        NowPlayingProgress.Progress = p;
        NowPlayingMore.Text = progress.Count > 1 ? $"+{progress.Count - 1}" : "";
        if (id == _nowPlayingId) return;
        _nowPlayingId = id;
        var tile = _tiles.Concat(_lobbyTiles).FirstOrDefault(t => t.FileName == id);
        var color = tile?.Color ?? Palette.For(id);
        NowPlayingTitle.Text = tile?.Title ?? _library.Sounds.FirstOrDefault(s => s.FileName == id)?.Title ?? Path.GetFileNameWithoutExtension(id);
        NowPlayingIcon.Background = Palette.Gradient(color);
        NowPlayingProgress.Fill = Palette.Solid(color);
        NowPlaying.ToolTip = string.Join(", ", progress.Keys.Select(k => _library.Sounds.FirstOrDefault(s => s.FileName == k)?.Title ?? Path.GetFileNameWithoutExtension(k)));
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
        var anim = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
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
        SpeakerIcon.Text = Volume.Value < 0.005 ? "" : Volume.Value < 0.34 ? "" : Volume.Value < 0.67 ? "" : "";
        Volume.ToolTip = Volume.Value > 1.0001 ? "Verstärkt – über 100 % kann es übersteuern" : null;
        _settings.Volume = Volume.Value;
        SaveSettingsSoon();
    }

    private void VolumeText_Click(object sender, MouseButtonEventArgs e) => Volume.Value = 1;

    private void Overlap_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) { _settings.Overlap = Overlap.IsChecked == true; SaveSettingsSoon(); }
    }

    // ---------- Kachelgröße ----------

    private void ApplyTileSize(string size, bool save = true)
    {
        // Mindestbreite, Höhe, Schrift, Symbol, max. Titelhöhe, Innenabstand – wie TileSize am Mac
        (double width, double height, double font, double icon, double title, double pad) = size switch
        {
            "small" => (150.0, 92.0, 13.0, 16.0, 36.0, 11.0),
            "large" => (236.0, 140.0, 16.0, 22.0, 44.0, 15.0),
            _ => (188.0, 112.0, 14.0, 18.0, 40.0, 13.0),
        };
        if (size is not ("small" or "large")) size = "medium";
        Resources["TileMinWidth"] = width;
        Resources["TileHeight"] = height;
        Resources["TileFontSize"] = font;
        Resources["TileIconSize"] = icon;
        Resources["TileTitleMaxHeight"] = title;
        Resources["TilePadding"] = new Thickness(pad + 2, pad, pad + 2, pad);
        SizeSmall.IsChecked = size == "small";
        SizeMedium.IsChecked = size == "medium";
        SizeLarge.IsChecked = size == "large";
        _loadingSettings = true;
        TileSizeBox.SelectedItem = TileSizeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == size);
        _loadingSettings = false;
        if (!save) return;
        _settings.TileSize = size;
        SaveSettingsSoon();
    }

    private static readonly string[] TileSizes = { "small", "medium", "large" };

    private void StepTileSize(int step)
    {
        int i = Array.IndexOf(TileSizes, _settings.TileSize);
        ApplyTileSize(TileSizes[Math.Clamp((i < 0 ? 1 : i) + step, 0, TileSizes.Length - 1)]);
    }

    private void TileSize_Click(object sender, RoutedEventArgs e) => ApplyTileSize((string)((MenuItem)sender).Tag);

    private void TileSizeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingSettings && TileSizeBox.SelectedItem is ComboBoxItem { Tag: string size }) ApplyTileSize(size);
    }

    private void View_Click(object sender, RoutedEventArgs e) => OpenMenu(ViewBtn);

    private static void OpenMenu(Button button)
    {
        var menu = button.ContextMenu!;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.Folder}\"") { UseShellExecute = true });

    // ---------- Import ----------

    private void Import_Click(object sender, RoutedEventArgs e) => OpenMenu(ImportBtn);

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

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" };
    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        DropText.Text = _section == Section.Settings ? "Loslassen: Bild wird das App-Icon" : "Loslassen zum Importieren";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // In den Einstellungen: ein Bild wird das App-Icon
        if (_section == Section.Settings && paths is [var one] && IsImage(one)) { SetIcon(one); return; }
        Import(paths);
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
        else if (result.added > 0 && _section != Section.Sounds && _section != Section.Lobby) Navigate(Section.Sounds);
    }

    // ---------- Kontextmenü ----------

    private static TileViewModel? TileFrom(object sender) => (sender as FrameworkElement)?.DataContext as TileViewModel;

    /// <summary>Kontextmenü je nach Lage: eigener Sound, als Host in der Lobby oder Sound des Hosts</summary>
    private void TileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is not TileViewModel tile) return;
        MenuItem Item(string header, string glyph, Action action)
        {
            var mi = new MenuItem { Header = header, Icon = Ui.Glyph(glyph, 14) };
            mi.Click += (_, _) => action();
            return mi;
        }
        menu.Items.Add(Item("Abspielen", "", () => Play(tile)));
        if (tile.Lobby is { } ls)
        {
            menu.Items.Add(Item("Behalten …", "", () => _lobby.Ask(ls)));
            return;
        }
        menu.Items.Add(Item("Taste festlegen …", "", () => StartRecording(tile)));
        menu.Items.Add(Item("Im Explorer zeigen", "", () =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{tile.Sound.Path}\"") { UseShellExecute = true })));
        if (_lobby.IsHost && _lobby.Guests.Any())
        {
            var gift = new MenuItem { Header = "Schenken an", Icon = Ui.Glyph("", 14) };
            foreach (var m in _lobby.Guests) gift.Items.Add(new MenuItem { Header = m.Name }.Also(mi => mi.Click += (_, _) => _lobby.Offer(tile.Sound, m)));
            menu.Items.Add(gift);
        }
        menu.Items.Add(new Separator());
        var delete = Item("Löschen …", "", () => Menu_Delete(tile));
        delete.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
        menu.Items.Add(delete);
    }
    private void Key_Click(object sender, RoutedEventArgs e) { if (TileFrom(sender) is { Lobby: null } t) StartRecording(t); e.Handled = true; }

    private async void Menu_Delete(TileViewModel t)
    {
        if (!await AskAsync($"„{t.Title}“ löschen?", "Die Datei wird in den Papierkorb verschoben.", "In den Papierkorb", danger: true)) return;
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
        RecorderKeys.Children.Clear();
        foreach (var part in bind?.Parts ?? new[] { "–" })
        {
            var cap = new Border { Child = new TextBlock { Text = part, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
            cap.SetResourceReference(StyleProperty, "KeyCap");
            if (bind is null)
            {
                // noch keine Taste: neutrale Kappe statt Akzentfarbe
                cap.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
                cap.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                cap.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            }
            RecorderKeys.Children.Add(cap);
        }
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
        if (modifiers == ModifierKeys.Control && key is Key.F or Key.O or Key.L or Key.OemComma)
        {
            Note("Strg+F, Strg+O, Strg+L und Strg+, sind schon vergeben (Suchen, Importieren, Überlappen, Einstellungen).");
            return;
        }

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
    private void DialogCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ---------- Rückfragen (wie ContentDialog) ----------

    private readonly SemaphoreSlim _dialogLock = new(1, 1);
    private TaskCompletionSource<bool>? _dialog;

    /// <summary>Frage im Fenster; mehrere warten nacheinander. Ist das Fenster versteckt, kommt es nach vorne.</summary>
    public async Task<bool> AskAsync(string title, string text, string primary, string? close = "Abbrechen", bool danger = false, bool password = false)
    {
        await _dialogLock.WaitAsync();
        try
        {
            if (!IsVisible) ShowFromTray();
            DialogTitle.Text = title;
            DialogText.Text = text;
            DialogPrimary.Content = primary;
            if (danger)
            {
                DialogPrimary.ClearValue(StyleProperty);
                DialogPrimary.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
            }
            else
            {
                DialogPrimary.SetResourceReference(StyleProperty, "AccentButtonStyle");
                DialogPrimary.ClearValue(ForegroundProperty);
            }
            DialogClose.Content = close;
            DialogClose.Visibility = close is null ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(DialogPrimary, close is null ? 3 : 1);
            DialogPassword.Clear();
            DialogPassword.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
            DialogLayer.Visibility = Visibility.Visible;
            _dialog = new TaskCompletionSource<bool>();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (password) DialogPassword.Focus(); else Keyboard.Focus(this); });
            return await _dialog.Task;
        }
        finally
        {
            _dialog = null;
            DialogLayer.Visibility = Visibility.Collapsed;
            Keyboard.Focus(this);
            _dialogLock.Release();
        }
    }

    /// <summary>Rückfrage mit Passwortfeld; null = abgebrochen</summary>
    public async Task<string?> AskPasswordAsync(string title, string text, string primary)
    {
        bool ok = await AskAsync(title, text, primary, danger: true, password: true);
        var pw = DialogPassword.Password;
        DialogPassword.Clear();
        return ok ? pw : null;
    }

    private void DialogPrimary_Click(object sender, RoutedEventArgs e) => _dialog?.TrySetResult(true);
    private void DialogClose_Click(object sender, RoutedEventArgs e) => _dialog?.TrySetResult(false);

    // ---------- Tastatur ----------

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Bei Alt-Kombinationen steckt die echte Taste in SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (_dialog is not null)
        {
            // Rückfrage offen: Esc = abbrechen, Enter = bestätigen, sonst nur tippen (keine Sounds)
            if (key == Key.Escape && DialogClose.Visibility == Visibility.Visible) { _dialog.TrySetResult(false); e.Handled = true; }
            else if (key == Key.Enter) { _dialog.TrySetResult(true); e.Handled = true; }
            return;
        }

        if (_recording is not null)
        {
            e.Handled = true;
            if (IsModifier(key)) return;
            if (key == Key.Escape && mods == ModifierKeys.None) StopRecording();
            else if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) RemoveKey_Click(this, e);
            else Record(key, mods);
            return;
        }

        if (mods == ModifierKeys.Control && key == Key.F) { FocusSearch(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && key == Key.O) { ImportFiles_Click(this, e); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && key == Key.OemComma) { Navigate(Section.Settings); e.Handled = true; return; }
        // Fensterbefehle wie am Mac – aber nur, wenn die Kombination nicht schon einem Sound gehört
        if (mods == ModifierKeys.Control && _keys.SoundFor(key, mods) is null)
        {
            if (key == Key.L) { Overlap.IsChecked = Overlap.IsChecked != true; e.Handled = true; return; }
            if (key is Key.OemPlus or Key.Add) { StepTileSize(1); e.Handled = true; return; }
            if (key is Key.OemMinus or Key.Subtract) { StepTileSize(-1); e.Handled = true; return; }
        }
        if (key == Key.Escape)
        {
            StopAll();
            if (Search.IsKeyboardFocused) Keyboard.Focus(this);
            e.Handled = true;
            return;
        }
        if (Search.IsKeyboardFocused)
        {
            var first = (_section == Section.Lobby && InGuestLobby ? _lobbyTiles : _tiles).FirstOrDefault();
            if (key == Key.Enter && first is not null) { Play(first); e.Handled = true; }
            return;
        }
        // z. B. Lobby-Code oder Passwort: Zahlen sollen ins Feld, nicht Sounds starten
        if (Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox) return;
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
    private LobbyPhase _lastPhase = LobbyPhase.Off;

    private void SetUpLobby()
    {
        LobbyName.Text = _settings.LobbyName ?? "";
        _lobby.GetSystemVolume = SystemVolume.Get;
        _lobby.SetSystemVolume = SystemVolume.Set;
        _lobby.Changed += () =>
        {
            // Lobby geöffnet, beigetreten oder Clan-Session gestartet: dorthin wechseln (wie am Mac)
            bool started = _lastPhase == LobbyPhase.Off && _lobby.Phase != LobbyPhase.Off;
            _lastPhase = _lobby.Phase;
            if (started && _section != Section.Lobby) Navigate(Section.Lobby);
            Render(); UpdateLobbyUi(); UpdateVolumeRows(); LobbyTestStep();
            _clanPage?.LobbyChanged();
        };
        _lobby.Note += ShowToast;
        _lobby.Ended += text =>
        {
            LobbyLog("ende: " + text);
            _lastPhase = LobbyPhase.Off;
            LobbyError.Text = text;
            LobbyError.Visibility = Visibility.Visible;
            if (_section != Section.Lobby) ShowError(text);
            Render(); UpdateLobbyUi();
        };
        _lobby.Play += (sound, delay) => PlayLocal(sound, delay, Path.GetFileNameWithoutExtension(sound.FileName));
        _lobby.Stop += () => { _player?.StopAll(); UpdateProgress(); };
        _lobby.Receive += path =>
        {
            LobbyLog("bekommen " + Path.GetFileName(path));
            Import(new[] { path });
        };
        _lobby.Request += async request =>
        {
            LobbyLog($"anfrage {(request.Asked ? "asked" : "offered")} {request.SoundName} von {request.By}");
            bool ok;
            if (TestMode) ok = HasArg("--lobby-accept");
            else ok = request.Asked
                ? await AskAsync($"{request.By} möchte „{request.SoundTitle}“ behalten", $"Der Sound wird in die Bibliothek von {request.By} kopiert.", "Erlauben", "Ablehnen")
                : await AskAsync($"{request.By} schenkt dir „{request.SoundTitle}“", "Der Sound wird in deine Sounds kopiert.", "Annehmen", "Ablehnen");
            _lobby.Answer(request, ok);
        };
        UpdateLobbyUi();
    }

    private string _codeShown = "";
    private string _membersShown = "";

    private void UpdateLobbyUi()
    {
        bool active = _lobby.Active;
        bool open = _lobby.Phase == LobbyPhase.Open;
        LobbySetup.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        LobbyInside.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        int n = _library.Sounds.Count;
        LobbyOpenHint.Text = n == 0 ? "Du hast noch keine Sounds zum Teilen." : $"Deine {n} Sounds werden für die anderen freigegeben.";
        LobbyOpenBtn.IsEnabled = !active;
        LobbyJoinBtn.IsEnabled = !active && LobbyCode.Text.Trim().Length == 5;

        // Leiste und Wiedergabeleiste
        LobbyNavDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LobbyBadge.Visibility = active && !_paneCompact ? Visibility.Visible : Visibility.Collapsed;
        LobbyChip.Visibility = LobbyChipDivider.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.ToolTip = active ? "Bei allen in der Lobby stoppen (Esc)" : "Alles stoppen (Esc)";
        if (!active) { _codeShown = _membersShown = ""; return; }

        var dotBrush = open ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush";
        LobbyNavDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotBrush);
        LobbyChipDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotBrush);
        LobbyStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotBrush);
        LobbyBadgeText.Text = LobbyChipCode.Text = _lobby.Code;
        LobbyChipCount.Text = _lobby.Members.Count.ToString();

        LobbyTitle.Text = _lobby.IsHost ? "Deine Lobby" : $"Lobby von {_lobby.HostName}";
        int people = _lobby.Members.Count;
        LobbyPeople.Text = people == 1 ? "1 Person" : $"{people} Personen";
        var others = _lobby.Members.Where(m => m.Id != _lobby.Me).Select(m => m.Name).ToList();
        LobbyStatus.Text = _lobby.Phase switch
        {
            LobbyPhase.Connecting => "Verbinde …",
            LobbyPhase.Reconnecting => "Verbindung weg – verbinde neu …",
            _ => others.Count == 0 ? "Noch niemand da – schick den Code rum" : "Mit " + string.Join(", ", others),
        };

        // Code als einzelne Felder, wie ein Bestätigungscode
        if (_lobby.Code != _codeShown)
        {
            _codeShown = _lobby.Code;
            CodeBoxes.Children.Clear();
            foreach (char c in _lobby.Code)
            {
                var t = Ui.Text(c.ToString(), 26, FontWeights.SemiBold, wrap: false);
                t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                var box = new Border { Width = 44, Height = 56, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = t };
                box.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
                box.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                CodeBoxes.Children.Add(box);
            }
        }

        // Leute: nur neu bauen, wenn sich jemand ändert
        var membersKey = string.Join("|", _lobby.Members.Select(m => m.Id + m.Name + m.Host));
        if (membersKey != _membersShown)
        {
            _membersShown = membersKey;
            MembersPanel.Children.Clear();
            foreach (var m in _lobby.Members)
            {
                var row = new DockPanel();
                var avatar = Ui.Avatar(m.Name, 34);
                avatar.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(avatar);
                if (m.Host)
                {
                    var star = Ui.Glyph("", 14, "SystemFillColorCautionBrush");
                    star.ToolTip = "Hat die Lobby geöffnet";
                    DockPanel.SetDock(star, Dock.Right);
                    row.Children.Add(star);
                }
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Ui.Text(m.Name + (m.Id == _lobby.Me ? " (du)" : ""), weight: FontWeights.SemiBold, wrap: false));
                text.Children.Add(Ui.Caption(m.Host ? "Hat die Lobby geöffnet" : "Hört mit"));
                row.Children.Add(text);
                var card = Ui.Card(row, 12);
                card.Width = 250;
                card.Margin = new Thickness(0, 0, 8, 8);
                MembersPanel.Children.Add(card);
            }
        }

        HostInfo.Visibility = _lobby.IsHost ? Visibility.Visible : Visibility.Collapsed;
        GuestSounds.Visibility = _lobby.IsHost ? Visibility.Collapsed : Visibility.Visible;
        if (_lobby.IsHost && _lobby.Uploaded is { } u && u.Done < u.Total)
        {
            UploadInfo.Visibility = Visibility.Visible;
            UploadProgress.Maximum = Math.Max(u.Total, 1);
            UploadProgress.Value = u.Done;
            UploadText.Text = $"{u.Done} von {u.Total}";
        }
        else UploadInfo.Visibility = Visibility.Collapsed;
        SearchPlaceholder.Text = _section == Section.Lobby && InGuestLobby ? "Lobby durchsuchen" : "Sounds suchen";
    }

    private string LobbyPersonName => LobbyName.Text.Trim() is { Length: > 0 } n ? n : Environment.UserName;

    private void LobbyChip_Click(object sender, RoutedEventArgs e) => Navigate(Section.Lobby);

    private void LobbyOpen_Click(object sender, RoutedEventArgs e)
    {
        LobbyError.Visibility = Visibility.Collapsed;
        _settings.LobbyName = LobbyName.Text.Trim();
        SaveSettingsSoon();
        _ = _lobby.OpenAsync(LobbyPersonName, _library.Sounds);
        UpdateLobbyUi();
    }

    private void LobbyJoin_Click(object sender, RoutedEventArgs e)
    {
        if (LobbyCode.Text.Trim().Length != 5) return;
        LobbyError.Visibility = Visibility.Collapsed;
        _settings.LobbyName = LobbyName.Text.Trim();
        SaveSettingsSoon();
        _lobby.Join(LobbyCode.Text, LobbyPersonName);
        UpdateLobbyUi();
    }

    private void LobbyCode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { LobbyJoin_Click(sender, e); e.Handled = true; }
    }

    /// <summary>Nur Buchstaben und Ziffern, höchstens 5 (Links und Leerzeichen beim Einfügen fallen weg)</summary>
    private void LobbyCode_TextChanged(object sender, TextChangedEventArgs e)
    {
        var clean = new string(LobbyCode.Text.Where(char.IsLetterOrDigit).Take(5).ToArray()).ToUpperInvariant();
        if (clean != LobbyCode.Text) { LobbyCode.Text = clean; LobbyCode.CaretIndex = clean.Length; return; }
        LobbyCodePlaceholder.Visibility = clean.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbyJoinBtn.IsEnabled = !_lobby.Active && clean.Length == 5;
    }

    private void LobbyCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_lobby.Code); ShowToast($"Code {_lobby.Code} kopiert"); } catch { }
    }

    private void LobbyLeave_Click(object sender, RoutedEventArgs e)
    {
        _lobby.Leave();
        _lastPhase = LobbyPhase.Off;
        Render();
        UpdateLobbyUi();
    }

    // ---------- Lautstärke der Geräte / des PCs (versteckt: 3× auf den Lautsprecher) ----------

    private bool _volumePopupForLobby;

    private void SpeakerIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3) return;
        _volumePopupForLobby = _lobby.Active;
        _volumeRowIds = "";
        if (_volumePopupForLobby) UpdateVolumeRows(); else BuildSystemVolume();
        VolumePopup.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Ohne Lobby: die echten Lautsprecher dieses PCs – nicht die App-Lautstärke</summary>
    private void BuildSystemVolume()
    {
        VolumePopupTitle.Text = "System-Lautstärke";
        VolumeRows.Children.Clear();
        _volumeRows.Clear();
        if (SystemVolume.Get() is not { } level)
        {
            VolumeHint.Text = "Dieses Ausgabegerät meldet keine Lautstärke (z. B. manche HDMI-Ausgänge).";
            return;
        }
        var value = Ui.Secondary($"{Math.Round(level * 100)} %");
        value.Width = 44;
        value.TextAlignment = TextAlignment.Right;
        var slider = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.02, LargeChange = 0.1, IsMoveToPointEnabled = true, Value = level, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { value.Text = $"{Math.Round(e.NewValue * 100)} %"; SystemVolume.Set(e.NewValue); };
        var row = new DockPanel();
        var low = Ui.Glyph("", 14, "TextFillColorSecondaryBrush");
        low.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(low, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(low);
        row.Children.Add(value);
        row.Children.Add(slider);
        VolumeRows.Children.Add(row);
        VolumeHint.Text = "Stellt die echten Lautsprecher dieses PCs ein – nicht die App-Lautstärke.";
    }

    private string _volumeRowIds = "";
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _volumeRows = new();

    /// <summary>Zeilen nur neu bauen, wenn sich die Leute ändern – sonst springt der Regler beim Ziehen</summary>
    private void UpdateVolumeRows()
    {
        if (!_volumePopupForLobby) return;
        if (!_lobby.Active) { VolumePopup.IsOpen = false; return; }
        VolumePopupTitle.Text = "Lautstärke der Geräte";
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
                var head = new DockPanel();
                var value = Ui.Secondary("–");
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                head.Children.Add(Ui.Text(m.Name + (me ? " (du)" : ""), wrap: false));
                row.Children.Add(head);
                if (m.Volume is null)
                    row.Children.Add(Ui.Caption("Dieses Gerät meldet keine Lautstärke."));
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
                    if (m.System == false) row.Children.Add(Ui.Caption("Nur die App – im Browser geht es nicht anders."));
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

    public void ShowToast(string text)
    {
        LobbyLog("note: " + text);
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- Einstellungen ----------

    private bool _loadingSettings;

    private void SetUpSettingsPage()
    {
        _loadingSettings = true;
        KeepInTray.IsChecked = _settings.KeepInTray;
        _loadingSettings = false;
        FolderText.Text = _library.Folder;
        VersionText.Text = $"ClipSound {Updater.CurrentVersion}";
        UpdateSettingsPage();
    }

    private void UpdateSettingsPage()
    {
        KeepInTrayHint.Text = _settings.KeepInTray
            ? "Das X schließt nur das Fenster. Tastenkürzel mit Alt oder Strg gehen weiter, ClipSound bleibt unten rechts im Infobereich."
            : "Das X beendet ClipSound. Danach gehen auch die Tastenkürzel nicht mehr.";
        var custom = CustomIcon.Load();
        IconPreview.Source = custom ?? new BitmapImage(new Uri("pack://application:,,,/AppIcon.ico"));
        IconState.Text = custom is null ? "App-Icon: Standard" : "App-Icon: eigenes Bild";
        ResetIcon.Visibility = custom is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void KeepInTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _settings is null) return; // kommt schon während InitializeComponent
        _settings.KeepInTray = KeepInTray.IsChecked == true;
        _settings.Save();
        UpdateSettingsPage();
    }

    private void SetIcon(string path)
    {
        if (!CustomIcon.Set(path)) { ShowError("Das ist kein Bild, das ClipSound lesen kann (PNG, JPG, BMP, GIF, ICO)."); return; }
        UpdateSettingsPage();
        ApplyIcon();
    }

    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Bild für das ClipSound-Icon", Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico" };
        if (dialog.ShowDialog(this) == true) SetIcon(dialog.FileName);
    }

    private void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        CustomIcon.Reset();
        UpdateSettingsPage();
        ApplyIcon();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e) => Relay.OpenInBrowser(Account.PrivacyUrl);

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!await CheckForUpdateAsync()) ShowToast($"Du hast die neueste Version ({Updater.CurrentVersion}).");
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
            _tray.ContextMenuStrip.Items.Add("Einstellungen …", null, (_, _) => { ShowFromTray(); Navigate(Section.Settings); });
            _tray.ContextMenuStrip.Items.Add("Beenden", null, (_, _) => { Quitting = true; Close(); });
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch { _tray = null; } // ohne Symbol wird beim Schließen einfach beendet
    }

    private System.Drawing.Icon? _defaultTrayIcon;

    /// <summary>Eigenes Icon (oder wieder das Standard-Icon) für Fenster, Taskleiste und Infobereich</summary>
    private void ApplyIcon()
    {
        var custom = CustomIcon.Load();
        Icon = custom ?? BitmapFrame.Create(new Uri("pack://application:,,,/AppIcon.ico"));
        if (_tray is null) return;
        _defaultTrayIcon ??= _tray.Icon;
        _tray.Icon = CustomIcon.TrayIcon() ?? _defaultTrayIcon;
    }

    /// <summary>Fenster nach vorne holen (auch aus dem Infobereich, z. B. wenn ClipSound ein zweites Mal gestartet wird)</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowError(string text)
    {
        if (TestMode) { _testErrors.Add(text); return; }
        // Versteckt im Infobereich: Windows-Meldung statt das Fenster aufzureißen
        if (!IsVisible) { MessageBox.Show(text, "ClipSound", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _ = AskAsync("ClipSound", text, "OK", close: null);
    }

    // ---------- Updates ----------

    private Updater.Release? _update;

    /// <summary>true, wenn es ein Update gibt (dann steht oben die Leiste)</summary>
    private async Task<bool> CheckForUpdateAsync()
    {
        try { _update = await Updater.CheckAsync(); }
        catch { return false; } // offline – egal, beim nächsten Start wieder
        if (_update is null) return false;
        UpdateTitle.Text = $"ClipSound {_update.Version} ist verfügbar.";
        UpdateSubtitle.Text = $"Du hast {Updater.CurrentVersion}.";
        UpdateBar.Visibility = Visibility.Visible;
        if (HasArg("--update-now")) UpdateNow_Click(this, new RoutedEventArgs());
        return true;
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
            // Testlauf: Grund festhalten (die Meldung kann hinter einer anderen warten)
            if (HasArg("--update-now")) try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "clipsound-update-fehler.txt"), ex.ToString()); } catch { }
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
        if (HasArg("--play-first") && _tiles.Count > 0)
        {
            // fürs Bild: zwei Sounds gleichzeitig, damit die Leiste „+1“ zeigt (ohne die Einstellung zu ändern)
            _loading = true;
            Overlap.IsChecked = true;
            _loading = false;
            foreach (var t in _tiles.Take(2).Reverse()) Play(t);
        }
        if (snapshot is null && selftest is null && !HasArg("--recorder")) return;

        var delay = Arg("--snapshot-delay") is { } d ? double.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : 1.5;
        var steps = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        steps.Tick += (_, _) =>
        {
            steps.Stop();
            if (HasArg("--recorder") && _tiles.Count > 0) StartRecording(_tiles[0]);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (snapshot is not null) SaveSnapshot(snapshot, this);
                if (selftest is not null) WriteSelftest(selftest);
                if (snapshot is not null || selftest is not null) Close();
            });
        };
        steps.Start();
    }

    private static void SaveSnapshot(string path, Window window)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        int w = (int)root.ActualWidth, h = (int)root.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Mica ist im Bild durchsichtig – darum mit der Fenster-Grundfarbe hinterlegen
            var bg = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
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
        sb.AppendLine($"Sounds: {_library.Sounds.Count}, Kacheln: {_tiles.Count}, Fenster: {ActualWidth}x{ActualHeight}, Leiste: {Pane.ActualWidth}");
        sb.AppendLine($"Audio: {(_player?.HasOutput == true ? "ok" : "kein Gerät – " + (_player?.OutputError ?? _playerError))}");
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

internal static class Extensions
{
    /// <summary>Objekt einrichten und zurückgeben (für kurze Menü-Einträge)</summary>
    public static T Also<T>(this T value, Action<T> setup) { setup(value); return value; }
}

/// <summary>Lautstärke, Überlappen und Kachelgröße merken (%AppData%\ClipSound\settings.json).</summary>
public sealed class Settings
{
    public double Volume { get; set; } = 0.8;
    public bool Overlap { get; set; }
    /// <summary>small, medium oder large (wie am Mac)</summary>
    public string TileSize { get; set; } = "medium";
    public string? LobbyName { get; set; }
    public bool TrayHintShown { get; set; }
    /// <summary>X = nur Fenster zu, weiter im Infobereich (sonst beenden)</summary>
    public bool KeepInTray { get; set; } = true;

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
