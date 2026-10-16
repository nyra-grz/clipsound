namespace ClipSound;

/// <summary>„Zusammen abspielen“: Lobby öffnen oder mit Code beitreten; drinnen Code, Leute, Verlassen.</summary>
public sealed class LobbyDialog
{
    private readonly Lobby _lobby;
    private readonly SoundLibrary _library;
    private readonly Settings _settings;
    private readonly Adw.Dialog _dialog = Adw.Dialog.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.EntryRow _name = Adw.EntryRow.New(), _code = Adw.EntryRow.New();
    private readonly Adw.ActionRow _openRow;
    private readonly Gtk.Button _openBtn;
    private readonly Gtk.Label _problem = Ui.Label("", "error");
    private readonly Gtk.Label _bigCode = Ui.Label("", "big-code"), _insideTitle = Ui.Label("", "dim-label");
    private readonly Adw.PreferencesGroup _members = Ui.Group("In der Lobby");
    private readonly Gtk.Label _hostHint = Ui.Label("Rechtsklick auf einen Sound → „Schenken an“, um ihn jemandem zu geben. Gäste können dich auch fragen – du entscheidest.", "dim-label", "caption");
    private readonly List<Gtk.Widget> _memberRows = new();
    private bool _closeWhenIn;

    public event Action? Closed;

    public LobbyDialog(Lobby lobby, SoundLibrary library, Settings settings, Func<Gtk.Window> window)
    {
        _lobby = lobby; _library = library; _settings = settings;
        _dialog.SetTitle("Zusammen abspielen");
        _dialog.SetContentWidth(480);
        _dialog.SetContentHeight(560);

        // ---------- Draußen: öffnen oder beitreten ----------
        _name.SetTitle("Dein Name");
        _name.SetText(settings.LobbyName ?? "");
        _name.OnChanged += (_, _) => { _settings.LobbyName = _name.GetText().Trim(); _settings.Save(); };
        _openBtn = Ui.Button("Öffnen", Open, "suggested-action");
        _openBtn.SetValign(Gtk.Align.Center);
        _openRow = Ui.Row("Eigene Lobby öffnen", "");
        _openRow.AddSuffix(_openBtn);
        _openRow.SetActivatableWidget(_openBtn);
        _code.SetTitle("Lobby-Code");
        _code.SetShowApplyButton(true);
        _code.OnApply += (_, _) => Join();
        _code.OnEntryActivated += (_, _) => Join();
        _problem.SetVisible(false);
        var outside = Ui.VBox(24,
            Ui.Label("Jeder Sound, den jemand drückt, läuft bei allen in der Lobby gleichzeitig – egal ob Linux, Windows, Mac, iPhone oder Browser.", "dim-label"),
            Ui.Group(null, null, _name),
            Ui.Group("Neue Lobby", null, _openRow),
            Ui.Group("Beitreten", "Code von dem, der die Lobby geöffnet hat (5 Zeichen).", _code),
            _problem).Margins(18);

        // ---------- Drinnen ----------
        _bigCode.SetXalign(0.5f);
        _bigCode.SetSelectable(true);
        _insideTitle.SetXalign(0.5f);
        var copy = Ui.Button("Code kopieren", () =>
        {
            window().GetClipboard().SetText(_lobby.Code);
            var t = Adw.Toast.New($"Code {_lobby.Code} kopiert");
            _toasts.AddToast(t);
        }, "pill");
        copy.SetHalign(Gtk.Align.Center);
        var leave = Ui.Button("Lobby verlassen", () => { _lobby.Leave(); Update(); }, "pill", "destructive-action");
        leave.SetHalign(Gtk.Align.Center);
        var inside = Ui.VBox(18, _insideTitle, _bigCode, copy, _members, _hostHint, leave).Margins(18);

        _stack.AddNamed(Scroll(outside), "outside");
        _stack.AddNamed(Scroll(inside), "inside");
        _stack.SetTransitionType(Gtk.StackTransitionType.Crossfade);
        _toasts.SetChild(_stack);
        var view = Adw.ToolbarView.New();
        view.AddTopBar(Adw.HeaderBar.New());
        view.SetContent(_toasts);
        _dialog.SetChild(view);
        _dialog.OnClosed += (_, _) => Closed?.Invoke();
        Update();
    }

    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();

    private static Gtk.ScrolledWindow Scroll(Gtk.Widget content)
    {
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(520);
        clamp.SetChild(content);
        var s = Gtk.ScrolledWindow.New();
        s.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        s.SetChild(clamp);
        return s;
    }

    private string PersonName => _name.GetText().Trim() is { Length: > 0 } n ? n : Environment.UserName;

    private void Open()
    {
        _problem.SetVisible(false);
        _ = _lobby.OpenAsync(PersonName, _library.Sounds);
        Update();
    }

    private void Join()
    {
        _problem.SetVisible(false);
        _closeWhenIn = true;
        _lobby.Join(_code.GetText(), PersonName);
        Update();
    }

    public void ShowProblem(string text)
    {
        _problem.SetText(text);
        _problem.SetVisible(true);
        Update();
    }

    public void Present(Gtk.Widget parent) => _dialog.Present(parent);

    public void Update()
    {
        bool active = _lobby.Active;
        _stack.SetVisibleChildName(active ? "inside" : "outside");
        int n = _library.Sounds.Count;
        _openRow.SetSubtitle(n == 0 ? "Du hast noch keine Sounds zum Teilen." : n == 1 ? "Dein Sound wird für die anderen freigegeben." : $"Deine {n} Sounds werden für die anderen freigegeben.");
        _openBtn.SetSensitive(!active);
        if (!active) return;
        _bigCode.SetText(_lobby.Code);
        _insideTitle.SetText(_lobby.Phase switch
        {
            LobbyPhase.Connecting => "Verbinde …",
            LobbyPhase.Reconnecting => "Verbindung weg – verbinde neu …",
            _ => _lobby.IsHost ? "Dein Lobby-Code – schick ihn rum" : $"Lobby von {_lobby.HostName}",
        });
        _hostHint.SetVisible(_lobby.IsHost);
        foreach (var r in _memberRows) _members.Remove(r);
        _memberRows.Clear();
        foreach (var m in _lobby.Members)
        {
            var row = Ui.Row(m.Name + (m.Id == _lobby.Me ? " (du)" : ""), m.Host ? "Host" : null);
            row.AddPrefix(Adw.Avatar.New(32, m.Name, true));
            _members.Add(row);
            _memberRows.Add(row);
        }
        // Gast: sobald drin, Dialog zu – die Sounds sieht man im Hauptfenster
        if (_closeWhenIn && !_lobby.IsHost && _lobby.Phase == LobbyPhase.Open) { _closeWhenIn = false; _dialog.Close(); }
    }
}
