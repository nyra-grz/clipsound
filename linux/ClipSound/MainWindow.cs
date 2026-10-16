using System.Runtime.InteropServices;
using System.Text;

namespace ClipSound;

// Startargumente (zum Testen):
//   --library <ordner>   anderen Sound-Ordner benutzen          --size <B>x<H>  Fenstergröße
//   --snapshot <png>     Fenster als Bild speichern und beenden  --selftest <txt> Wiedergabe/Tasten prüfen, Bericht schreiben
//   --import-test <ord>  (mit --selftest) Dateien importieren    --recorder      Aufnahme-Dialog für den ersten Sound öffnen
//   --settings / --account / --lobby / --about   Dialog öffnen (mit --snapshot: davon das Bild)
//   --pretend-version <v> so tun, als wäre Version <v> installiert   --update-now   verfügbares Update gleich installieren
//   --lobby-host / --lobby-join <code> / --lobby-accept / --lobby-play-first / --lobby-log <txt> / --lobby-exit-after <s>
//   --hotkey-test <txt>  Alt+2 auf den ersten Sound legen, 15 s auf den systemweiten Kürzel warten, Ergebnis schreiben
//   Server ändern: Umgebungsvariable CLIPSOUND_SERVER
/// <summary>Hauptfenster nach GNOME-Muster: Kopfleiste mit Symbolknöpfen, Banner, Kacheln als Karten, Toasts.</summary>
public sealed class MainWindow
{
    public static bool TestMode { get; } = HasArg("--snapshot") || HasArg("--selftest") || HasArg("--lobby-host") || HasArg("--lobby-join") || HasArg("--hotkey-test");

    public Adw.ApplicationWindow Window { get; }
    private readonly Adw.Application _app;
    private readonly SoundLibrary _library;
    private readonly KeyBindStore _keys;
    private readonly SoundPlayer _player = new();
    private readonly Settings _settings = Settings.Load();
    private readonly Lobby _lobby;
    private readonly Account _account;
    private readonly GlobalHotKeys _hotKeys = new();
    private readonly List<string> _testErrors = new();

    private readonly Adw.WindowTitle _title = Adw.WindowTitle.New("ClipSound", "");
    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.FlowBox _flow = Gtk.FlowBox.New();
    private readonly Gtk.SearchBar _searchBar = Gtk.SearchBar.New();
    private readonly Gtk.SearchEntry _search = Gtk.SearchEntry.New();
    private readonly Gtk.ToggleButton _searchToggle = Gtk.ToggleButton.New();
    private readonly Gtk.Button _stopBtn;
    private readonly Gtk.MenuButton _importBtn = Gtk.MenuButton.New();
    private readonly Gtk.Scale _volume = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0, SoundPlayer.MaxVolume, 0.05);
    private readonly Gtk.Label _volumeLabel = Gtk.Label.New("");
    private readonly Gtk.Switch _overlap = Gtk.Switch.New();
    private readonly Gtk.Box _mixer = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private readonly Adw.Banner _updateBanner = Adw.Banner.New("");
    private readonly Adw.Banner _lobbyBanner = Adw.Banner.New("");
    private readonly Adw.StatusPage _empty = Adw.StatusPage.New(), _noResults = Adw.StatusPage.New(), _waiting = Adw.StatusPage.New();
    private List<TileCard> _tiles = new();
    private string _tilesKey = "";
    private bool _recording;
    private bool _quitting, _held;
    private uint _progressTimer;
    private LobbyDialog? _lobbyDialog;

    public MainWindow(Adw.Application app)
    {
        _app = app;
        _lobby = new Lobby();
        _library = new SoundLibrary(Arg("--library"));
        _keys = new KeyBindStore(Path.Combine(_library.SettingsFolder, "keybinds.json"));
        _account = new Account(Path.Combine(_library.SettingsFolder, "account.token"));
        _account.Attach(_library); // Clan-Sounds von diesem PC aus weitergeben
        _library.Changed += () =>
        {
            _lobby.HostLibraryChanged(_library.Sounds); // als Host: Liste in der Lobby aktuell halten
            Ui.OnMain(() => { Render(); RegisterHotKeys(); }); // auch Sounds aus dem Clan-Dialog sofort zeigen
        };

        Window = Adw.ApplicationWindow.New(app);
        Window.SetTitle("ClipSound");
        Window.SetIconName("clipsound");
        int width = 1000, height = 680;
        if (Arg("--size")?.Split('x') is [var w, var h]) { width = int.Parse(w); height = int.Parse(h); }
        Window.SetDefaultSize(width, height);
        Window.SetSizeRequest(360, 420);

        // ---------- Kopfleiste ----------
        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(_title);

        var importMenu = Gio.Menu.New();
        importMenu.Append("Dateien auswählen …", "app.import-files");
        importMenu.Append("Ganzen Ordner importieren …", "app.import-folder");
        importMenu.Append("Sound-Ordner öffnen", "app.reveal");
        _importBtn.SetIconName("list-add-symbolic");
        _importBtn.SetTooltipText("Sounds importieren (Strg+O)");
        _importBtn.SetMenuModel(importMenu);
        header.PackStart(_importBtn);
        _stopBtn = Ui.IconButton("media-playback-stop-symbolic", "Alles stoppen (Esc)", StopAll);
        header.PackStart(_stopBtn);

        var mainMenu = Gio.Menu.New();
        var section = Gio.Menu.New();
        section.Append("Einstellungen", "app.preferences");
        section.Append("Datenschutzerklärung", "app.privacy");
        section.Append("Über ClipSound", "app.about");
        mainMenu.AppendSection(null, section);
        var quitSection = Gio.Menu.New();
        quitSection.Append("Beenden", "app.quit");
        mainMenu.AppendSection(null, quitSection);
        var menuBtn = Gtk.MenuButton.New();
        menuBtn.SetIconName("open-menu-symbolic");
        menuBtn.SetTooltipText("Hauptmenü");
        menuBtn.SetMenuModel(mainMenu);
        menuBtn.SetPrimary(true); // F10
        header.PackEnd(menuBtn);

        _searchToggle.SetIconName("system-search-symbolic");
        _searchToggle.SetTooltipText("Suchen (Strg+F)");
        _searchToggle.OnToggled += (_, _) => { _searchBar.SetSearchMode(_searchToggle.GetActive()); if (_searchToggle.GetActive()) _search.GrabFocus(); };
        header.PackEnd(_searchToggle);

        header.PackEnd(BuildVolumeButton());
        header.PackEnd(Ui.IconButton("avatar-default-symbolic", "Konto & Clans", OpenAccount));
        header.PackEnd(Ui.IconButton("system-users-symbolic", "Zusammen abspielen (Lobby)", OpenLobby));

        // ---------- Suche, Banner ----------
        _search.SetPlaceholderText("Sounds suchen");
        _search.OnSearchChanged += (_, _) => Render();
        _search.OnActivate += (_, _) => { if (_tiles.Count > 0) Play(_tiles[0]); };
        _search.OnStopSearch += (_, _) => { _searchToggle.SetActive(false); Window.SetFocus(null); };
        var searchClamp = Adw.Clamp.New();
        searchClamp.SetMaximumSize(480);
        searchClamp.SetChild(_search);
        _searchBar.SetChild(searchClamp);
        _searchBar.ConnectEntry(_search);
        _searchBar.OnNotify += (_, e) => { if (e.Pspec.GetName() == "search-mode-enabled") _searchToggle.SetActive(_searchBar.GetSearchMode()); };

        _updateBanner.SetButtonLabel("Aktualisieren");
        _updateBanner.OnButtonClicked += (_, _) => _ = InstallUpdateAsync();
        _lobbyBanner.SetButtonLabel("Verlassen");
        _lobbyBanner.OnButtonClicked += (_, _) => { _lobby.Leave(); Render(); UpdateLobbyUi(); };

        // ---------- Inhalt ----------
        _flow.SetHomogeneous(true);
        _flow.SetMinChildrenPerLine(1);
        _flow.SetMaxChildrenPerLine(24);
        _flow.SetSelectionMode(Gtk.SelectionMode.None);
        // Abstände wie am Mac: 10 zwischen den Karten, 16 Rand (das GTK-Polster der Kinder nimmt .sound-grid weg)
        _flow.AddCssClass("sound-grid");
        _flow.SetColumnSpacing(10);
        _flow.SetRowSpacing(10);
        _flow.SetValign(Gtk.Align.Start);
        _flow.Margins(16);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetChild(_flow);

        _empty.SetIconName("audio-x-generic-symbolic");
        _empty.SetTitle("Keine Sounds");
        _empty.SetDescription("Zieh Sound-Dateien oder einen ganzen Ordner in dieses Fenster.");
        var emptyButtons = Ui.VBox(12,
            Ui.Button("Sounds importieren …", () => _ = ImportFilesAsync(), "pill", "suggested-action"),
            Ui.Button("Ordner importieren …", () => _ = ImportFolderAsync(), "pill"));
        emptyButtons.SetHalign(Gtk.Align.Center);
        _empty.SetChild(emptyButtons);
        _noResults.SetIconName("system-search-symbolic");
        _noResults.SetTitle("Keine Treffer");
        _waiting.SetIconName("system-users-symbolic");

        _stack.AddNamed(scroll, "tiles");
        _stack.AddNamed(_empty, "empty");
        _stack.AddNamed(_noResults, "noresults");
        _stack.AddNamed(_waiting, "waiting");
        _stack.SetTransitionType(Gtk.StackTransitionType.Crossfade);
        _stack.SetVexpand(true);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.AddTopBar(_searchBar);
        view.AddTopBar(_updateBanner);
        view.AddTopBar(_lobbyBanner);
        view.SetContent(_stack);
        _toasts.SetChild(view);
        Window.SetContent(_toasts);

        // ---------- Ereignisse ----------
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) => OnKeyPressed(e.Keyval, e.State);
        Window.AddController(keys);
        SetUpDrop();
        _hotKeys.Pressed += id => Ui.OnMain(() =>
        {
            if (!_recording && _library.Sounds.FirstOrDefault(s => s.FileName == id) is { } sound) Play(new TileCard(sound));
        });
        Window.OnCloseRequest += (_, _) => OnCloseRequest();
        AddActions();
        SetUpLobby();

        _volume.SetValue(Math.Clamp(_settings.Volume, 0, SoundPlayer.MaxVolume));
        _overlap.SetActive(_settings.Overlap);
        ApplyVolume();
        Render();
        RegisterHotKeys();
    }

    private static string? Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasArg(string name) => Environment.GetCommandLineArgs().Contains(name);

    public void Present()
    {
        Window.SetVisible(true);
        Window.Present();
    }

    /// <summary>Nach dem ersten Zeigen: Updates, Audio-Hinweis, Testläufe</summary>
    public void Started()
    {
        RunTestArguments();
        if (!TestMode || HasArg("--pretend-version")) _ = CheckForUpdateAsync();
        if (!_player.HasOutput)
            ShowError("Kein Audiogerät gefunden", $"{_player.OutputError}\n\nSobald Lautsprecher oder Kopfhörer da sind, spielt ClipSound von selbst darauf.");
    }

    // ---------- Aktionen (Menüs, Tastenkürzel) ----------

    private void AddActions()
    {
        void Add(string name, Action a, params string[] accels)
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => a();
            _app.AddAction(action);
            if (accels.Length > 0) _app.SetAccelsForAction("app." + name, accels);
        }
        Add("import-files", () => _ = ImportFilesAsync(), "<Control>o");
        Add("import-folder", () => _ = ImportFolderAsync());
        Add("reveal", _library.Reveal);
        Add("preferences", OpenSettings, "<Control>comma");
        Add("privacy", () => Open.Url(Account.PrivacyUrl.ToString()));
        Add("about", ShowAbout);
        Add("quit", Quit, "<Control>q");
        Add("search", () => _searchToggle.SetActive(!_searchToggle.GetActive()), "<Control>f");
    }

    // ---------- Lautstärke (Aufklappmenü in der Kopfleiste) ----------

    private Gtk.MenuButton BuildVolumeButton()
    {
        _volume.SetDrawValue(false);
        _volume.AddMark(1, Gtk.PositionType.Bottom, "100 %");
        _volume.SetHexpand(true);
        _volume.SetSizeRequest(240, -1);
        _volume.OnValueChanged += (_, _) => ApplyVolume();
        _volumeLabel.AddCssClass("numeric");
        _volumeLabel.AddCssClass("dim-label");
        var heading = Ui.Label("Lautstärke", "heading");
        heading.SetHexpand(true);
        // versteckt: 3× auf „Lautstärke“ klicken zeigt in einer Lobby die Lautstärke der anderen Geräte
        var triple = Gtk.GestureClick.New();
        triple.OnPressed += (_, e) => { if (e.NPress == 3 && _lobby.Active) BuildMixer(); };
        heading.AddController(triple);
        var top = Ui.HBox(6, heading, _volumeLabel);
        var reset = Ui.Button("100 %", () => _volume.SetValue(1), "flat");
        _overlap.SetValign(Gtk.Align.Center);
        _overlap.OnStateSet += (_, e) => { _settings.Overlap = e.State; _settings.Save(); return false; };
        var overlapLabel = Ui.Label("Mehrere gleichzeitig");
        overlapLabel.SetHexpand(true);
        var overlapRow = Ui.HBox(12, overlapLabel, _overlap);
        _mixer.SetVisible(false);
        var box = Ui.VBox(10, top, _volume, reset, Gtk.Separator.New(Gtk.Orientation.Horizontal), overlapRow, _mixer).Margins(12);
        reset.SetHalign(Gtk.Align.Start);
        var popover = Gtk.Popover.New();
        popover.SetChild(box);
        var button = Gtk.MenuButton.New();
        button.SetIconName("audio-volume-high-symbolic");
        button.SetTooltipText("Lautstärke (bis 500 %)");
        button.SetPopover(popover);
        return button;
    }

    private void ApplyVolume()
    {
        var v = _volume.GetValue();
        _player.Volume = v;
        _volumeLabel.SetText($"{Math.Round(v * 100)} %");
        if (v > 1.0001) _volumeLabel.AddCssClass("warning"); else _volumeLabel.RemoveCssClass("warning");
        _settings.Volume = v;
        SaveSoon();
    }

    private uint _saveTimer;
    private void SaveSoon()
    {
        if (_saveTimer != 0) return;
        _saveTimer = Ui.Every(TimeSpan.FromSeconds(1), () => { _saveTimer = 0; _settings.Save(); return false; });
    }

    /// <summary>Lautstärke der anderen Geräte in der Lobby (stellt deren echte Lautsprecher)</summary>
    private void BuildMixer()
    {
        while (_mixer.GetFirstChild() is { } c) _mixer.Remove(c);
        _mixer.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        _mixer.Append(Ui.Label("Lautstärke der Geräte", "heading"));
        foreach (var m in _lobby.Members)
        {
            bool me = m.Id == _lobby.Me;
            _mixer.Append(Ui.Label((m.Host ? "Host · " : "") + m.Name + (me ? " (du)" : ""), "caption"));
            if (m.Volume is not { } v) { _mixer.Append(Ui.Label("meldet keine Lautstärke", "dim-label", "caption")); continue; }
            var s = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0, 1, 0.02);
            s.SetValue(v);
            s.SetDrawValue(false);
            var member = m;
            s.OnValueChanged += (_, _) => { if (me) _lobby.SetOwnVolume(s.GetValue()); else _lobby.SetVolume(member, s.GetValue()); };
            _mixer.Append(s);
        }
        _mixer.SetVisible(true);
    }

    // ---------- Liste ----------

    /// <summary>Gast in einer fremden Lobby: dann zeigt das Fenster die Sounds des Hosts</summary>
    private bool InGuestLobby => _lobby.Active && !_lobby.IsHost;

    private void Render()
    {
        _keys.Sync(_library.Sounds);
        var q = (_search.GetText() ?? "").Trim();
        string page;
        if (InGuestLobby)
        {
            var lobbySounds = _lobby.Sounds.Where(s => q.Length == 0 || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            var key = "lobby|" + string.Join("|", lobbySounds.Select(s => s.Id + (s.Local is null ? "-" : "+")));
            if (key != _tilesKey) { _tilesKey = key; SetTiles(lobbySounds.Select(s => new TileCard(s.Local ?? new Sound(s.Name), s))); }
            int count = _lobby.Sounds.Count;
            _title.SetSubtitle($"Lobby von {_lobby.HostName} · " + (count == 1 ? "1 Sound" : $"{count} Sounds"));
            _waiting.SetTitle(_lobby.Phase == LobbyPhase.Open ? "Noch keine Sounds" : "Verbinde …");
            _waiting.SetDescription(_lobby.Phase == LobbyPhase.Open ? $"{_lobby.HostName} hat noch keine Sounds freigegeben." : "Einen Moment.");
            _noResults.SetDescription($"Nichts passt zu „{q}“.");
            page = count == 0 ? "waiting" : _tiles.Count == 0 ? "noresults" : "tiles";
        }
        else
        {
            var sounds = q.Length == 0 ? _library.Sounds
                : _library.Sounds.Where(s => s.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            var key = "own|" + string.Join("|", sounds.Select(s => s.Path));
            if (key != _tilesKey) { _tilesKey = key; SetTiles(sounds.Select(s => new TileCard(s))); }
            int n = _library.Sounds.Count;
            _title.SetSubtitle(n == 1 ? "1 Sound" : $"{n} Sounds");
            _noResults.SetDescription($"Nichts passt zu „{q}“.");
            page = n == 0 ? "empty" : _tiles.Count == 0 ? "noresults" : "tiles";
        }
        _stack.SetVisibleChildName(page);
        _importBtn.SetSensitive(!InGuestLobby);
        UpdateKeyBadges();
        UpdateProgress();
    }

    private void SetTiles(IEnumerable<TileCard> tiles)
    {
        _tiles = tiles.ToList();
        _flow.RemoveAll();
        foreach (var t in _tiles)
        {
            t.PlayRequested += Play;
            t.MenuRequested += ShowMenu;
            _flow.Append(t.Widget);
        }
    }

    private void UpdateKeyBadges()
    {
        foreach (var t in _tiles)
        {
            if (t.Lobby is not null) { t.SetKey(null, "local"); continue; }
            var state = _hotKeys.Active.Contains(t.FileName) ? "global" : _hotKeys.Failed.Contains(t.FileName) ? "failed" : "local";
            t.SetKey(_keys.Binds.GetValueOrDefault(t.FileName), state);
        }
    }

    private void RegisterHotKeys()
    {
        if (_recording) return;
        _hotKeys.Register(_keys.Binds);
        UpdateKeyBadges();
    }

    // ---------- Abspielen ----------

    private void Play(TileCard tile)
    {
        // Lobby: läuft über den Server, damit es bei allen gleichzeitig startet
        if (tile.Lobby is not null) { _lobby.PlayGuest(tile.Lobby); return; }
        if (_lobby.Active && _lobby.IsHost)
        {
            if (_lobby.PlayOwn(tile.Sound)) return;
            Toast($"„{tile.Title}“ ist noch nicht in der Lobby – nur bei dir abgespielt.");
        }
        PlayLocal(tile.Sound, TimeSpan.Zero, tile.Title);
    }

    private void PlayLocal(Sound sound, TimeSpan delay, string title)
    {
        LobbyLog($"play {sound.FileName} in {(int)delay.TotalMilliseconds} ms");
        if (!_overlap.GetActive()) _player.StopAll();
        try
        {
            _player.Play(sound, delay);
            if (_progressTimer == 0) _progressTimer = Ui.Every(TimeSpan.FromMilliseconds(33), () => { UpdateProgress(); return _progressTimer != 0; });
        }
        catch (Exception ex)
        {
            ShowError($"„{title}“ konnte nicht abgespielt werden", ex.Message);
        }
    }

    private void StopAll()
    {
        _player.StopAll();
        if (_lobby.Active) _lobby.StopAll();
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var progress = _player.Progress();
        foreach (var t in _tiles) t.SetProgress(progress.TryGetValue(t.FileName, out var p) ? p : null);
        _stopBtn.SetSensitive(progress.Count > 0 || _lobby.Active);
        if (progress.Count == 0) _progressTimer = 0; // Timer endet beim nächsten Tick
    }

    // ---------- Import ----------

    private Gtk.FileDialog SoundDialog(string title)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle(title);
        var filter = Gtk.FileFilter.New();
        filter.SetName("Sounds");
        foreach (var ext in SoundLibrary.AudioExtensions) filter.AddSuffix(ext.TrimStart('.'));
        filter.AddMimeType("audio/*");
        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(filter);
        dialog.SetFilters(filters);
        dialog.SetDefaultFilter(filter);
        return dialog;
    }

    private async Task ImportFilesAsync()
    {
        try { Import(Ui.Paths(await SoundDialog("Sounds importieren").OpenMultipleAsync(Window))); }
        catch { /* abgebrochen */ }
    }

    private async Task ImportFolderAsync()
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle("Ordner mit Sounds auswählen");
        try { Import(Ui.Paths(await dialog.SelectMultipleFoldersAsync(Window))); }
        catch { /* abgebrochen */ }
    }

    private void Import(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var (added, rejected) = _library.Import(paths);
        Render();
        RegisterHotKeys();
        if (rejected.Count > 0)
        {
            var list = string.Join("\n", rejected.Take(5)) + (rejected.Count > 5 ? $"\n… und {rejected.Count - 5} weitere" : "");
            ShowError("Einige Dateien wurden übersprungen", $"Das sind keine Sounds, die ClipSound abspielen kann:\n\n{list}");
        }
        else if (added > 0) Toast(added == 1 ? "1 Sound importiert" : $"{added} Sounds importiert");
    }

    // Dateien ins Fenster ziehen. GirCore hat für Gdk.FileList keine Bindung – darum direkt
    [DllImport("gtk")] private static extern IntPtr gdk_file_list_get_files(IntPtr list);
    [DllImport("gio")] private static extern IntPtr g_file_get_path(IntPtr file);
    [DllImport("glib")] private static extern void g_free(IntPtr p);
    [DllImport("glib")] private static extern void g_slist_free(IntPtr list);

    private void SetUpDrop()
    {
        NativeNames.Install();
        var drop = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy);
        drop.OnEnter += (_, _) => { _stack.AddCssClass("drop-target"); return Gdk.DragAction.Copy; };
        drop.OnLeave += (_, _) => _stack.RemoveCssClass("drop-target");
        drop.OnDrop += (_, e) =>
        {
            _stack.RemoveCssClass("drop-target");
            var paths = new List<string>();
            try
            {
                var list = gdk_file_list_get_files(e.Value.GetBoxed());
                for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    var path = g_file_get_path(Marshal.ReadIntPtr(node));
                    if (path == IntPtr.Zero) continue;
                    paths.Add(Marshal.PtrToStringUTF8(path)!);
                    g_free(path);
                }
                g_slist_free(list);
            }
            catch { return false; }
            if (InGuestLobby || paths.Count == 0) return false;
            Import(paths);
            return true;
        };
        _stack.AddController(drop);
    }

    // ---------- Kontextmenü ----------

    private void ShowMenu(TileCard tile, double x, double y)
    {
        var group = Gio.SimpleActionGroup.New();
        var menu = Gio.Menu.New();
        void Item(string label, string name, Action a)
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => a();
            group.AddAction(action);
            menu.Append(label, "tile." + name);
        }
        Item("Abspielen", "play", () => Play(tile));
        if (tile.Lobby is { } ls)
            Item("Behalten …", "keep", () => _lobby.Ask(ls));
        else
        {
            Item("Taste festlegen …", "key", () => StartRecording(tile));
            Item("Im Dateimanager zeigen", "reveal", _library.Reveal);
            if (_lobby.IsHost && _lobby.Guests.Any())
            {
                var gifts = Gio.Menu.New();
                int i = 0;
                foreach (var m in _lobby.Guests)
                {
                    var member = m;
                    var action = Gio.SimpleAction.New($"gift{i}", null);
                    action.OnActivate += (_, _) => _lobby.Offer(tile.Sound, member);
                    group.AddAction(action);
                    gifts.Append(m.Name, $"tile.gift{i++}");
                }
                menu.AppendSubmenu("Schenken an", gifts);
            }
            var danger = Gio.Menu.New();
            var del = Gio.SimpleAction.New("delete", null);
            del.OnActivate += (_, _) => _ = DeleteAsync(tile);
            group.AddAction(del);
            danger.Append("In den Papierkorb …", "tile.delete");
            menu.AppendSection(null, danger);
        }
        tile.Widget.InsertActionGroup("tile", group);
        var popover = Gtk.PopoverMenu.NewFromModel(menu);
        popover.SetParent(tile.Widget);
        popover.SetHasArrow(false);
        popover.SetPointingTo(new Gdk.Rectangle { X = (int)x, Y = (int)y, Width = 1, Height = 1 });
        popover.OnClosed += (_, _) => Ui.OnMain(popover.Unparent);
        popover.Popup();
    }

    private async Task DeleteAsync(TileCard t)
    {
        if (!await Ui.Ask(Window, $"„{t.Title}“ löschen?", "Der Sound kommt in den Papierkorb. Von dort kannst du ihn zurückholen.", "In den Papierkorb", destructive: true)) return;
        StopAll();
        _keys.Set(t.FileName, null);
        _library.Delete(t.Sound);
        Render();
        RegisterHotKeys();
        Toast($"„{t.Title}“ ist im Papierkorb");
    }

    // ---------- Taste aufnehmen ----------

    private void StartRecording(TileCard tile)
    {
        if (tile.Lobby is not null) return; // Sounds des Hosts haben keine Tasten
        _hotKeys.UnregisterAll(); // sonst würde eine schon vergebene Kombi abspielen statt aufnehmen
        _recording = true;
        var dialog = Adw.Dialog.New();
        dialog.SetTitle("Taste festlegen");
        dialog.SetContentWidth(420);
        var key = Ui.Label("", "key-preview");
        key.SetXalign(0.5f);
        key.SetHalign(Gtk.Align.Center);
        void Show() => key.SetText(_keys.Binds.GetValueOrDefault(tile.FileName)?.Display ?? "–");
        Show();
        var note = Ui.Label("", "warning");
        note.SetVisible(false);
        note.SetJustify(Gtk.Justification.Center);
        note.SetXalign(0.5f);
        var hint = Ui.Label(GlobalHotKeys.IsWayland
            ? "Unter Wayland gehen Tastenkürzel nur im ClipSound-Fenster – im Hintergrund erlaubt Wayland das nicht. In einer X11-Sitzung gehen Alt+Taste und Strg+Umschalt+Taste auch in Spielen."
            : "Mit Alt (z. B. Alt+2) oder Strg+Umschalt geht sie überall, auch wenn ClipSound im Hintergrund ist – z. B. in Spielen oder Discord.", "dim-label");
        hint.SetJustify(Gtk.Justification.Center);
        hint.SetXalign(0.5f);
        var remove = Ui.Button("Taste entfernen", () => { _keys.Set(tile.FileName, null); dialog.Close(); }, "pill");
        var done = Ui.Button("Fertig", () => dialog.Close(), "pill", "suggested-action");
        var buttons = Ui.HBox(12, remove, done);
        buttons.SetHalign(Gtk.Align.Center);
        var body = Ui.VBox(18,
            Ui.Label($"Drück eine Taste oder Kombination für „{tile.Title}“.", "title-4"),
            key, note, hint, buttons).Margins(24);
        foreach (var l in new[] { (Gtk.Label)body.GetFirstChild()! }) { l.SetJustify(Gtk.Justification.Center); l.SetXalign(0.5f); }
        remove.SetFocusable(false); done.SetFocusable(false);
        var page = Adw.ToolbarView.New();
        page.AddTopBar(Adw.HeaderBar.New());
        page.SetContent(body);
        dialog.SetChild(page);

        var capture = Gtk.EventControllerKey.New();
        capture.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        capture.OnKeyPressed += (_, e) =>
        {
            var k = e.Keyval;
            if (IsModifier(k)) return true;
            var mods = KeyBind.ModsFrom(e.State);
            if (k == 0xff1b && mods == 0) { dialog.Close(); return true; } // Esc
            if (k is 0xff08 or 0xffff && mods == 0) { _keys.Set(tile.FileName, null); dialog.Close(); return true; } // Rücktaste/Entf
            if (e.State.HasFlag(Gdk.ModifierType.SuperMask)) { Note("Kombinationen mit der Super-Taste gehören dem System."); return true; }
            if (mods == KeyBind.Ctrl && KeyBind.Normalize(k) is 'f' or 'o' or 'q') { Note("Strg+F, Strg+O und Strg+Q sind schon vergeben (Suchen, Importieren, Beenden)."); return true; }
            var bind = KeyBind.From(k, e.State);
            bool altGr = (bind.Mods & (KeyBind.Ctrl | KeyBind.Alt)) == (KeyBind.Ctrl | KeyBind.Alt) && (bind.Mods & KeyBind.Shift) == 0;
            var previous = _keys.Set(tile.FileName, bind);
            Show();
            if (altGr) { Note("Strg+Alt ist AltGr (für @, € …) – geht nur im ClipSound-Fenster. Nimm Alt allein, z. B. Alt+2."); return true; }
            if (previous is not null)
            {
                var name = _library.Sounds.FirstOrDefault(s => s.FileName == previous)?.Title ?? previous;
                Note($"{bind.Display} war bei „{name}“ – dort ist sie jetzt entfernt.");
                return true;
            }
            dialog.Close();
            return true;
        };
        void Note(string text) { note.SetText(text); note.SetVisible(true); }
        dialog.AddController(capture);
        dialog.OnClosed += (_, _) => { _recording = false; RegisterHotKeys(); };
        dialog.Present(Window);
    }

    private static bool IsModifier(uint k) => k is >= 0xffe1 and <= 0xffee || k is 0xfe03 or 0 or 0xff7e; // Umschalt, Strg, Alt, Super, AltGr

    // ---------- Tastatur ----------

    private bool OnKeyPressed(uint keyval, Gdk.ModifierType state)
    {
        if (_recording) return false;
        if (keyval == 0xff1b) // Esc
        {
            if (_searchBar.GetSearchMode()) { _searchToggle.SetActive(false); return true; }
            StopAll();
            return true;
        }
        var focus = Window.GetFocus();
        if (focus is Gtk.Text || focus is Gtk.Editable) return false; // in Eingabefeldern tippen statt Sounds starten
        if (IsModifier(keyval)) return false;
        var id = _keys.SoundFor(keyval, state);
        if (id is null || _hotKeys.Active.Contains(id)) return false; // globale Kürzel kommen schon über X11
        var tile = _tiles.FirstOrDefault(t => t.FileName == id) ?? (_library.Sounds.FirstOrDefault(s => s.FileName == id) is { } s ? new TileCard(s) : null);
        if (tile is null) return false;
        Play(tile);
        return true;
    }

    // ---------- Lobby ----------

    private bool _playedFirst;

    private void SetUpLobby()
    {
        _lobby.GetSystemVolume = SystemVolume.Get;
        _lobby.SetSystemVolume = SystemVolume.Set;
        _lobby.Changed += () => { Render(); UpdateLobbyUi(); _lobbyDialog?.Update(); LobbyTestStep(); };
        _lobby.Note += Toast;
        _lobby.Ended += text =>
        {
            LobbyLog("ende: " + text);
            if (_lobbyDialog is not null) _lobbyDialog.ShowProblem(text); else ShowError("Lobby", text);
            Render(); UpdateLobbyUi();
        };
        _lobby.Play += (sound, delay) => PlayLocal(sound, delay, Path.GetFileNameWithoutExtension(sound.FileName));
        _lobby.Stop += () => { _player.StopAll(); UpdateProgress(); };
        _lobby.Receive += path => { LobbyLog("bekommen " + Path.GetFileName(path)); Import(new[] { path }); };
        _lobby.Request += async request =>
        {
            LobbyLog($"anfrage {(request.Asked ? "asked" : "offered")} {request.SoundName} von {request.By}");
            bool ok = TestMode ? HasArg("--lobby-accept") : await Ui.Ask(Window,
                request.Asked ? $"{request.By} möchte „{request.SoundTitle}“ behalten" : $"{request.By} schenkt dir „{request.SoundTitle}“",
                request.Asked ? $"Der Sound wird bei {request.By} gespeichert." : "Der Sound kommt in deine Sounds.",
                request.Asked ? "Erlauben" : "Annehmen", no: "Ablehnen");
            _lobby.Answer(request, ok);
        };
        UpdateLobbyUi();
    }

    private void UpdateLobbyUi()
    {
        bool active = _lobby.Active;
        _lobbyBanner.SetRevealed(active);
        if (!active) return;
        var others = _lobby.Members.Where(m => m.Id != _lobby.Me).Select(m => m.Name).ToList();
        string who = _lobby.Phase switch
        {
            LobbyPhase.Connecting => "verbinde …",
            LobbyPhase.Reconnecting => "Verbindung weg – verbinde neu …",
            _ => others.Count == 0 ? "noch niemand da – schick den Code rum" : "mit " + string.Join(", ", others),
        };
        _lobbyBanner.SetTitle(GLib.Markup.EscapeText($"{(_lobby.IsHost ? "Deine Lobby" : $"Lobby von {_lobby.HostName}")} {_lobby.Code} · {who}"));
    }

    private void OpenLobby()
    {
        _lobbyDialog = new LobbyDialog(_lobby, _library, _settings, () => Window);
        _lobbyDialog.Closed += () => _lobbyDialog = null;
        _lobbyDialog.Present(Window);
    }

    // ---------- Dialoge, Fenster ----------

    private void OpenAccount() => new AccountDialog(_account, _lobby, _library).Present(Window);

    private void OpenSettings() => SettingsDialog.Show(Window, _settings, _library, _player, _hotKeys, BackgroundChanged);

    private void BackgroundChanged()
    {
        if (!_settings.KeepInBackground && _held) { _app.Release(); _held = false; }
    }

    private void ShowAbout()
    {
        var about = Adw.AboutDialog.New();
        about.SetApplicationName("ClipSound");
        about.SetApplicationIcon("clipsound");
        about.SetVersion(Updater.CurrentVersion);
        about.SetDeveloperName("Daniel Özdek");
        about.SetComments("Meme-Sounds per Klick oder Taste – bis 500 %, zusammen in Lobbys und Clans mit Windows, Mac, iPhone und Browser.");
        about.SetWebsite($"https://github.com/{Updater.Repo}");
        about.SetIssueUrl($"https://github.com/{Updater.Repo}/issues");
        about.SetSupportUrl(Account.PrivacyUrl.ToString());
        about.SetLicenseType(Gtk.License.Custom);
        about.SetLicense("Datenschutzerklärung: " + Account.PrivacyUrl);
        about.Present(Window);
    }

    public void Toast(string text)
    {
        LobbyLog("note: " + text);
        var t = Adw.Toast.New(GLib.Markup.EscapeText(text));
        t.SetTimeout(3);
        _toasts.AddToast(t);
    }

    private void ShowError(string heading, string body)
    {
        if (TestMode) { _testErrors.Add(heading + ": " + body); return; }
        Ui.Tell(Window, heading, body);
    }

    /// <summary>X schließt nur das Fenster, wenn ClipSound im Hintergrund weiterlaufen soll (Tastenkürzel gehen weiter)</summary>
    private bool OnCloseRequest()
    {
        if (!_quitting && !TestMode && _settings.KeepInBackground)
        {
            Window.SetVisible(false);
            if (!_held) { _app.Hold(); _held = true; }
            _settings.Save();
            Toast("ClipSound läuft im Hintergrund weiter");
            return true;
        }
        Cleanup();
        return false;
    }

    public void Quit()
    {
        _quitting = true;
        Cleanup();
        if (_held) { _app.Release(); _held = false; }
        Window.Destroy();
        _app.Quit();
    }

    private bool _cleaned;
    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _settings.Save();
        if (_lobby.Active) _lobby.Leave();
        _hotKeys.Dispose();
        _player.Dispose();
    }

    // ---------- Updates ----------

    private Updater.Release? _update;

    private async Task CheckForUpdateAsync()
    {
        try { _update = await Updater.CheckAsync(); }
        catch { return; } // offline – egal, beim nächsten Start wieder
        if (_update is null) return;
        _updateBanner.SetTitle($"ClipSound {_update.Version} ist verfügbar (du hast {Updater.CurrentVersion})");
        _updateBanner.SetRevealed(true);
        if (HasArg("--update-now")) await InstallUpdateAsync();
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is null) return;
        _updateBanner.SetButtonLabel(null);
        try
        {
            await Updater.InstallAsync(_update, new Progress<double>(p => _updateBanner.SetTitle($"Wird heruntergeladen … {p:P0}")), Quit);
        }
        catch (Exception ex)
        {
            _updateBanner.SetTitle($"ClipSound {_update.Version} ist verfügbar");
            _updateBanner.SetButtonLabel("Aktualisieren");
            ShowError("Update fehlgeschlagen", ex.Message);
        }
    }

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
        if (HasArg("--lobby-play-first") && !_playedFirst && !_lobby.IsHost && _lobby.Phase == LobbyPhase.Open && _lobby.Sounds.Count > 0 && ready == _lobby.Sounds.Count)
        {
            _playedFirst = true;
            var first = _lobby.Sounds[0];
            _lobby.PlayGuest(first);
            Ui.After(TimeSpan.FromSeconds(1), () => _lobby.Ask(first));
        }
    }

    private string PersonName => _settings.LobbyName is { Length: > 0 } n ? n : Environment.UserName;

    private void RunTestArguments()
    {
        if (HasArg("--lobby-host")) _ = _lobby.OpenAsync(PersonName, _library.Sounds);
        if (Arg("--lobby-join") is { } code) _lobby.Join(code, PersonName);
        if (Arg("--lobby-exit-after") is { } secs)
            Ui.After(TimeSpan.FromSeconds(double.Parse(secs, System.Globalization.CultureInfo.InvariantCulture)), () =>
            {
                LobbyLog($"ende bibliothek={_library.Sounds.Count} [{string.Join(", ", _library.Sounds.Select(x => x.FileName))}]");
                Quit();
            });
        if (Arg("--hotkey-test") is { } hotkeyFile && _library.Sounds.Count > 0)
        {
            // Alt+2 auf den ersten Sound legen und 15 s auf den systemweiten Kürzel warten (CI drückt ihn mit xdotool)
            var first = _library.Sounds[0].FileName;
            _keys.Set(first, new KeyBind('2', KeyBind.Alt, "2"));
            RegisterHotKeys();
            File.WriteAllText(hotkeyFile, $"Alt+2 global: {_keys.Binds[first].IsGlobal}, registriert: {_hotKeys.Active.Contains(first)}, fehlgeschlagen: {_hotKeys.Failed.Contains(first)}\n");
            string? fired = null;
            _hotKeys.Pressed += id => fired = id;
            var started = DateTime.Now;
            Ui.Every(TimeSpan.FromMilliseconds(200), () =>
            {
                if (fired is null && DateTime.Now - started < TimeSpan.FromSeconds(15)) return true;
                File.AppendAllText(hotkeyFile, (fired is null ? "NICHT ausgelöst" : $"ausgelöst für {fired}") + "\n");
                Quit();
                return false;
            });
        }
        if (HasArg("--settings")) OpenSettings();
        if (HasArg("--account") || HasArg("--open-clan")) OpenAccount();
        if (HasArg("--lobby")) OpenLobby();
        if (HasArg("--about")) ShowAbout();
        var snapshot = Arg("--snapshot");
        var selftest = Arg("--selftest");
        if (snapshot is null && selftest is null && !HasArg("--recorder")) return;
        Ui.After(TimeSpan.FromSeconds(1.5), async () =>
        {
            if (HasArg("--recorder") && _tiles.Count > 0) StartRecording(_tiles[0]);
            if (selftest is not null) await WriteSelftestAsync(selftest);
            Ui.After(TimeSpan.FromMilliseconds(600), () =>
            {
                if (snapshot is not null) Ui.SaveSnapshot(Window, snapshot);
                if (snapshot is not null || selftest is not null) Quit();
            });
        });
    }

    private async Task WriteSelftestAsync(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sounds: {_library.Sounds.Count}, Kacheln: {_tiles.Count}, Fenster: {Window.GetWidth()}x{Window.GetHeight()}");
        sb.AppendLine($"GTK {Gtk.Functions.GetMajorVersion()}.{Gtk.Functions.GetMinorVersion()}.{Gtk.Functions.GetMicroVersion()}, libadwaita {Adw.Functions.GetMajorVersion()}.{Adw.Functions.GetMinorVersion()}.{Adw.Functions.GetMicroVersion()}");
        sb.AppendLine($"Audio: {(_player.HasOutput ? "ok über " + _player.OutputName : "kein Gerät – " + _player.OutputError)}");
        sb.AppendLine($"ffmpeg: {(Ffmpeg.Available ? "da" : "nicht installiert")}");
        sb.AppendLine($"Globale Tastenkürzel: {(_hotKeys.Supported ? "X11" : GlobalHotKeys.IsWayland ? "Wayland – nur im Fenster" : "nicht verfügbar")}");
        if (Arg("--import-test") is { } importDir)
        {
            var (added, rejected) = _library.Import(new[] { importDir });
            sb.AppendLine($"Import: {added} übernommen, {rejected.Count} abgelehnt");
            foreach (var r in rejected) sb.AppendLine("  abgelehnt: " + r);
            Render();
        }
        if (_library.Sounds.Count > 0)
        {
            var first = _library.Sounds[0];
            try
            {
                _player.Play(first);
                await Task.Delay(400);
                var p = _player.Progress().GetValueOrDefault(first.FileName);
                sb.AppendLine($"Wiedergabe {first.FileName}: {(p > 0 ? $"läuft ({p:P0} nach 0,4 s)" : "steht still")}");
                _player.StopAll();
            }
            catch (Exception ex) { sb.AppendLine($"Wiedergabe {first.FileName}: Fehler – {ex.Message}"); }
            _keys.Set(first.FileName, new KeyBind('k', KeyBind.Ctrl | KeyBind.Shift, "K"));
            RegisterHotKeys();
            sb.AppendLine($"Strg+Umschalt+K global: aktiv={_hotKeys.Active.Contains(first.FileName)} fehlgeschlagen={_hotKeys.Failed.Contains(first.FileName)}");
            var reloaded = new KeyBindStore(Path.Combine(_library.SettingsFolder, "keybinds.json"));
            sb.AppendLine($"Nach Neuladen: {reloaded.Binds.GetValueOrDefault(first.FileName)?.Display}");
            sb.AppendLine("Tasten: " + string.Join(", ", _library.Sounds.Take(6).Select(s => $"{s.Title}={_keys.Binds.GetValueOrDefault(s.FileName)?.Display ?? "–"}")));
        }
        if (_testErrors.Count > 0) sb.AppendLine("Meldungen: " + string.Join(" | ", _testErrors));
        File.WriteAllText(path, sb.ToString());
    }
}

/// <summary>Bibliotheksnamen für die eigenen DllImports (gtk, gio, glib) – Linux und (zum Testen) macOS mit Homebrew</summary>
internal static class NativeNames
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeNames).Assembly, (name, _, _) =>
        {
            string[] candidates = name switch
            {
                "gtk" => new[] { "libgtk-4.so.1", "libgtk-4.1.dylib", "/opt/homebrew/lib/libgtk-4.1.dylib", "/usr/local/lib/libgtk-4.1.dylib" },
                "gio" => new[] { "libgio-2.0.so.0", "libgio-2.0.0.dylib", "/opt/homebrew/lib/libgio-2.0.0.dylib", "/usr/local/lib/libgio-2.0.0.dylib" },
                "glib" => new[] { "libglib-2.0.so.0", "libglib-2.0.0.dylib", "/opt/homebrew/lib/libglib-2.0.0.dylib", "/usr/local/lib/libglib-2.0.0.dylib" },
                _ => Array.Empty<string>(),
            };
            foreach (var c in candidates) if (NativeLibrary.TryLoad(c, out var h)) return h;
            return IntPtr.Zero; // Standardsuche (z. B. PulseAudio, ALSA, X11)
        });
    }
}
