namespace ClipSound;

/// <summary>Kleine GTK/libadwaita-Bausteine, damit die Fenster kurz und einheitlich bleiben.</summary>
public static class Ui
{
    /// <summary>Feste Farbe pro Sound – aus der GNOME-Akzentpalette, gleiche Farbe bei Host und Gast</summary>
    public const int Tints = 8;

    public static int TintFor(string name)
    {
        uint h = 0;
        foreach (char c in name) unchecked { h = h * 31 + c; }
        return (int)(h % Tints);
    }

    /// <summary>App-eigenes CSS: nur, wo libadwaita nichts Passendes hat (Kachelfarben, Tasten-Kapsel, Fortschritt)</summary>
    public static void InstallCss()
    {
        var colors = new[] { "#3584e4", "#2190a4", "#3a944a", "#c88800", "#ed5b00", "#e62d42", "#d56199", "#9141ac" };
        var css = new System.Text.StringBuilder("""
            .sound-grid > flowboxchild { padding: 0; }
            .sound-tile { padding: 12px 14px; }
            .sound-tile .tile-title { font-weight: 700; }
            .sound-tile .key-cap { font-family: monospace; font-size: 0.85em; padding: 1px 7px; border-radius: 6px; background: alpha(currentColor, 0.08); }
            .sound-tile .key-cap.failed { color: @warning_color; }
            .sound-tile .key-cap.global { color: @accent_color; background: alpha(@accent_bg_color, 0.14); }
            .sound-tile progressbar trough { min-height: 3px; background: none; box-shadow: none; }
            .sound-tile progressbar progress { min-height: 3px; border-radius: 3px; }
            .sound-tile.loading { opacity: 0.5; }
            .drop-target { box-shadow: inset 0 0 0 3px alpha(@accent_bg_color, 0.6); background: alpha(@accent_bg_color, 0.06); }
            .big-code { font-family: monospace; font-size: 2.4em; font-weight: 800; letter-spacing: 0.08em; }
            .key-preview { font-family: monospace; font-size: 1.8em; font-weight: 700; padding: 10px 22px; border-radius: 12px; background: alpha(currentColor, 0.08); }
            .online-dot { min-width: 10px; min-height: 10px; border-radius: 5px; background: #33d17a; }
            .offline-dot { min-width: 10px; min-height: 10px; border-radius: 5px; background: alpha(currentColor, 0.25); }
            """);
        for (int i = 0; i < colors.Length; i++)
            css.Append($$"""
                .tint-{{i}} .tint-icon { color: {{colors[i]}}; }
                .tint-{{i}} progressbar progress { background: {{colors[i]}}; }
                .tint-{{i}}.playing { box-shadow: inset 0 0 0 2px {{colors[i]}}; }
                """);
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(css.ToString());
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, provider, 800); // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
    }

    public static Gtk.Label Label(string text, params string[] classes)
    {
        var l = Gtk.Label.New(text);
        l.SetXalign(0);
        l.SetWrap(true);
        foreach (var c in classes) l.AddCssClass(c);
        return l;
    }

    public static Gtk.Button Button(string label, Action onClick, params string[] classes)
    {
        var b = Gtk.Button.NewWithLabel(label);
        foreach (var c in classes) b.AddCssClass(c);
        b.OnClicked += (_, _) => onClick();
        return b;
    }

    public static Gtk.Button IconButton(string icon, string tooltip, Action onClick, params string[] classes)
    {
        var b = Gtk.Button.NewFromIconName(icon);
        b.SetTooltipText(tooltip);
        foreach (var c in classes) b.AddCssClass(c);
        b.OnClicked += (_, _) => onClick();
        return b;
    }

    public static Gtk.Box Box(Gtk.Orientation o, int spacing, params Gtk.Widget[] children)
    {
        var box = Gtk.Box.New(o, spacing);
        foreach (var c in children) box.Append(c);
        return box;
    }

    /// <summary>Kopf wie eine kompakte Statusseite (Symbol, Titel, Text) – aber ohne eigenen Scrollbereich,
    /// damit nichts abgeschnitten wird, wenn sie in einer scrollenden Seite steckt</summary>
    public static Gtk.Box Hero(string icon, string title, string description)
    {
        var image = Gtk.Image.NewFromIconName(icon);
        image.SetPixelSize(64);
        image.AddCssClass("dim-label");
        var heading = Label(title, "title-2");
        var text = Label(description, "dim-label");
        heading.SetXalign(0.5f);
        heading.SetJustify(Gtk.Justification.Center);
        text.SetXalign(0.5f);
        text.SetJustify(Gtk.Justification.Center);
        var box = VBox(6, image, heading, text);
        image.SetMarginBottom(12);
        box.SetMarginTop(12);
        return box;
    }

    public static Gtk.Box VBox(int spacing, params Gtk.Widget[] children) => Box(Gtk.Orientation.Vertical, spacing, children);
    public static Gtk.Box HBox(int spacing, params Gtk.Widget[] children) => Box(Gtk.Orientation.Horizontal, spacing, children);

    public static T Margins<T>(this T w, int all) where T : Gtk.Widget
    {
        w.SetMarginTop(all); w.SetMarginBottom(all); w.SetMarginStart(all); w.SetMarginEnd(all);
        return w;
    }

    /// <summary>Zeile in einer Liste (libadwaita „boxed list“)</summary>
    public static Adw.ActionRow Row(string title, string? subtitle = null, Action? onActivate = null)
    {
        var row = Adw.ActionRow.New();
        row.SetTitle(GLib.Markup.EscapeText(title));
        if (subtitle is not null) row.SetSubtitle(GLib.Markup.EscapeText(subtitle));
        if (onActivate is not null)
        {
            row.SetActivatable(true);
            row.OnActivated += (_, _) => onActivate();
        }
        return row;
    }

    public static Adw.PreferencesGroup Group(string? title = null, string? description = null, params Gtk.Widget[] rows)
    {
        var g = Adw.PreferencesGroup.New();
        if (title is not null) g.SetTitle(GLib.Markup.EscapeText(title));
        if (description is not null) g.SetDescription(GLib.Markup.EscapeText(description));
        foreach (var r in rows) g.Add(r);
        return g;
    }

    /// <summary>Schwebender, scrollbarer Inhalt mit Kopfleiste (für Dialoge)</summary>
    public static Adw.ToolbarView Page(Gtk.Widget content, Gtk.Widget? title = null)
    {
        var view = Adw.ToolbarView.New();
        var header = Adw.HeaderBar.New();
        if (title is not null) header.SetTitleWidget(title);
        view.AddTopBar(header);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(560);
        clamp.SetChild(content);
        scroll.SetChild(clamp);
        view.SetContent(scroll);
        return view;
    }

    /// <summary>Frage mit zwei Antworten. true = bestätigt.</summary>
    public static Task<bool> Ask(Gtk.Widget parent, string heading, string body, string yes, bool destructive = false, string no = "Abbrechen", Gtk.Widget? extra = null)
    {
        var d = Adw.AlertDialog.New(heading, body);
        d.AddResponse("no", no);
        d.AddResponse("yes", yes);
        d.SetResponseAppearance("yes", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
        d.SetDefaultResponse("yes");
        d.SetCloseResponse("no");
        if (extra is not null) d.SetExtraChild(extra);
        var done = new TaskCompletionSource<bool>();
        d.OnResponse += (_, e) => done.TrySetResult(e.Response == "yes");
        d.Present(parent);
        return done.Task;
    }

    /// <summary>Meldung mit OK</summary>
    public static void Tell(Gtk.Widget parent, string heading, string body)
    {
        var d = Adw.AlertDialog.New(heading, body);
        d.AddResponse("ok", "OK");
        d.Present(parent);
    }

    /// <summary>Text abfragen (z. B. Passwort); null = abgebrochen</summary>
    public static async Task<string?> Prompt(Gtk.Widget parent, string heading, string body, string yes, bool password, bool destructive = false)
    {
        Gtk.Widget entry;
        Func<string> read;
        if (password) { var p = Gtk.PasswordEntry.New(); p.SetShowPeekIcon(true); entry = p; read = () => p.GetText(); }
        else { var t = Gtk.Entry.New(); entry = t; read = () => t.GetText(); }
        var ok = await Ask(parent, heading, body, yes, destructive, extra: entry);
        return ok ? read() : null;
    }

    /// <summary>Aus einem anderen Thread zurück auf den GTK-Hauptthread</summary>
    public static void OnMain(Action a) => GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () => { a(); return false; });

    /// <summary>Einmal nach einer Wartezeit ausführen (Hauptthread)</summary>
    public static void After(TimeSpan delay, Action a) => GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, (uint)delay.TotalMilliseconds, () => { a(); return false; });

    /// <summary>Wiederholt ausführen, bis die Funktion false zurückgibt</summary>
    public static uint Every(TimeSpan interval, Func<bool> f) => GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, (uint)interval.TotalMilliseconds, () => f());

    /// <summary>Pfade aus einer Dateiliste (Dateiauswahl)</summary>
    public static List<string> Paths(Gio.ListModel? list)
    {
        var paths = new List<string>();
        if (list is null) return paths;
        for (uint i = 0; i < list.GetNItems(); i++)
            if (list.GetObject(i) is Gio.File f && f.GetPath() is { } p) paths.Add(p);
        return paths;
    }

    /// <summary>Widget als PNG speichern (für Tests und Screenshots)</summary>
    public static void SaveSnapshot(Gtk.Widget widget, string path)
    {
        int w = Math.Max(1, widget.GetWidth()), h = Math.Max(1, widget.GetHeight());
        var paintable = Gtk.WidgetPaintable.New(widget);
        var snap = Gtk.Snapshot.New();
        paintable.Snapshot(snap, w, h);
        var node = snap.ToNode();
        var renderer = widget.GetNative()?.GetRenderer();
        if (node is null || renderer is null) return;
        var rect = Graphene.Rect.Alloc().Init(0, 0, w, h);
        renderer.RenderTexture(node, rect).SaveToPng(path);
    }
}
