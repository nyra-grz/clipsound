namespace ClipSound;

/// <summary>
/// Eine Sound-Kachel als libadwaita-Karte: farbiges Symbol, Titel, Taste oben rechts, dünner Fortschrittsbalken.
/// Klick spielt ab, Rechtsklick öffnet das Menü.
/// </summary>
public sealed class TileCard
{
    public Sound Sound { get; }
    /// <summary>Sound des Hosts, wenn man als Gast in einer Lobby ist</summary>
    public LobbySound? Lobby { get; }
    public string FileName => Sound.FileName;
    public string Title => Lobby?.Title ?? Sound.Title;
    public Gtk.Button Widget { get; }

    public event Action<TileCard>? PlayRequested;
    public event Action<TileCard, double, double>? MenuRequested;

    private readonly Gtk.Image _icon;
    private readonly Gtk.Label _key;
    private readonly Gtk.ProgressBar _progress;
    private bool _playing;

    public TileCard(Sound sound, LobbySound? lobby = null)
    {
        Sound = sound;
        Lobby = lobby;

        _icon = Gtk.Image.NewFromIconName("audio-x-generic-symbolic");
        _icon.AddCssClass("tint-icon");
        _icon.SetHalign(Gtk.Align.Start);
        _key = Gtk.Label.New("");
        _key.AddCssClass("key-cap");
        _key.SetHalign(Gtk.Align.End);
        _key.SetHexpand(true);
        _key.SetVisible(false);
        var top = Ui.HBox(6, _icon, _key);

        var title = Gtk.Label.New(Title);
        title.AddCssClass("tile-title");
        title.SetXalign(0);
        title.SetWrap(true);
        title.SetWrapMode(Pango.WrapMode.WordChar);
        title.SetLines(2);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        title.SetVexpand(true);
        title.SetValign(Gtk.Align.End);

        _progress = Gtk.ProgressBar.New();
        _progress.SetVisible(false);

        var content = Ui.VBox(6, top, title, _progress);
        Widget = Gtk.Button.New();
        Widget.SetChild(content);
        Widget.AddCssClass("card");
        Widget.AddCssClass("sound-tile");
        Widget.AddCssClass("tint-" + Ui.TintFor(lobby?.Name ?? sound.FileName)); // nach dem Originalnamen: Host und Gast sehen dieselbe Farbe
        if (IsLoading) Widget.AddCssClass("loading");
        Widget.SetSizeRequest(170, 86);
        Widget.SetTooltipText(lobby?.Name ?? sound.FileName);
        Widget.OnClicked += (_, _) => PlayRequested?.Invoke(this);

        // Rechtsklick (und langes Drücken auf Touchscreens) → Menü
        var right = Gtk.GestureClick.New();
        right.SetButton(3);
        right.OnPressed += (_, e) => MenuRequested?.Invoke(this, e.X, e.Y);
        Widget.AddController(right);
        var press = Gtk.GestureLongPress.New();
        press.OnPressed += (_, e) => MenuRequested?.Invoke(this, e.X, e.Y);
        Widget.AddController(press);
    }

    /// <summary>Gast: Datei wird noch geladen</summary>
    public bool IsLoading => Lobby is not null && Lobby.Local is null;

    public void SetKey(KeyBind? bind, string state)
    {
        _key.SetText(bind?.Display ?? "");
        _key.SetVisible(bind is not null);
        if (state == "failed") _key.AddCssClass("failed"); else _key.RemoveCssClass("failed");
        _key.SetTooltipText(state switch
        {
            "global" => "Geht auch im Hintergrund – klicken zum Ändern über Rechtsklick → Taste festlegen",
            "failed" => "Diese Kombination nutzt schon eine andere App – geht nur im ClipSound-Fenster",
            _ => "Geht im ClipSound-Fenster",
        });
        if (state == "global") _key.AddCssClass("global"); else _key.RemoveCssClass("global");
    }

    public void SetProgress(double? progress)
    {
        bool playing = progress is not null;
        if (playing != _playing)
        {
            _playing = playing;
            _progress.SetVisible(playing);
            if (playing) Widget.AddCssClass("playing"); else Widget.RemoveCssClass("playing");
            _icon.SetFromIconName(playing ? "audio-volume-high-symbolic" : "audio-x-generic-symbolic");
        }
        if (progress is { } p) _progress.SetFraction(Math.Clamp(p, 0, 1));
    }
}
