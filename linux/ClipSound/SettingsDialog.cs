namespace ClipSound;

/// <summary>Einstellungen als libadwaita-Einstellungsfenster: Fenster, App-Icon, Sounds/Ton, Tastenkürzel, Datenschutz.</summary>
public static class SettingsDialog
{
    public static void Show(Gtk.Widget parent, Settings settings, SoundLibrary library, SoundPlayer player, GlobalHotKeys hotKeys, Action backgroundChanged)
    {
        var dialog = Adw.PreferencesDialog.New();
        dialog.SetTitle("Einstellungen");
        var page = Adw.PreferencesPage.New();
        page.SetTitle("Allgemein");
        page.SetIconName("emblem-system-symbolic");

        var keep = Adw.SwitchRow.New();
        keep.SetTitle("Im Hintergrund weiterlaufen");
        keep.SetSubtitle("Das Fenster schließen beendet ClipSound nicht – Tastenkürzel gehen weiter. Zurück holst du es, indem du ClipSound noch einmal startest. Beenden mit Strg+Q.");
        keep.SetActive(settings.KeepInBackground);
        keep.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "active") return;
            settings.KeepInBackground = keep.GetActive();
            settings.Save();
            backgroundChanged();
        };
        page.Add(Ui.Group("Fenster", null, keep));
        page.Add(IconGroup(parent, dialog));

        var folder = Ui.Row("Sound-Ordner", library.Folder, library.Reveal);
        folder.SetSubtitleSelectable(true);
        folder.AddSuffix(Gtk.Image.NewFromIconName("folder-open-symbolic"));
        var output = Ui.Row("Ton", player.HasOutput ? $"Läuft über {player.OutputName}" : "Kein Ausgabegerät gefunden – sobald eins da ist, geht es los");
        var formats = Ui.Row("Formate", Ffmpeg.Available
            ? "MP3, WAV, OGG, AIFF – dazu FLAC, M4A, AAC und Opus über ffmpeg"
            : "MP3, WAV, OGG und AIFF. Für FLAC, M4A, AAC und Opus ffmpeg installieren (z. B. sudo apt install ffmpeg).");
        page.Add(Ui.Group("Sounds", null, folder, output, formats));

        var keys = Ui.Row("Tastenkürzel im Hintergrund", hotKeys.Supported
            ? "Kürzel mit Alt (z. B. Alt+2) oder Strg+Umschalt gehen überall, auch in Spielen."
            : GlobalHotKeys.IsWayland
                ? "Unter Wayland gehen Tastenkürzel nur, solange das ClipSound-Fenster aktiv ist. In einer X11-Sitzung (z. B. „Ubuntu on Xorg“ beim Anmelden) gehen sie auch im Hintergrund."
                : "Systemweite Tastenkürzel sind hier nicht verfügbar – sie gehen im ClipSound-Fenster.");
        page.Add(Ui.Group("Tastatur", "Taste ändern: Rechtsklick auf einen Sound → Taste festlegen.", keys));

        var privacy = Ui.Row("Datenschutzerklärung", "Der Server speichert keine Sounds – sie gehen direkt von Gerät zu Gerät.", () => Open.Url(Account.PrivacyUrl.ToString()));
        privacy.AddSuffix(Gtk.Image.NewFromIconName("adw-external-link-symbolic"));
        page.Add(Ui.Group("Datenschutz", null, privacy));

        dialog.Add(page);
        dialog.Present(parent);
    }

    /// <summary>Eigenes App-Icon wählen (Dateiauswahl oder Bild auf die Zeile ziehen) oder zurück zum Standard</summary>
    private static Adw.PreferencesGroup IconGroup(Gtk.Widget parent, Adw.PreferencesDialog dialog)
    {
        var row = Adw.ActionRow.New();
        var preview = CustomIcon.Preview(48);
        preview.SetMarginTop(8);
        preview.SetMarginBottom(8);
        row.AddPrefix(preview);

        void Use(string path)
        {
            if (!CustomIcon.Set(path)) dialog.AddToast(Adw.Toast.New("Das ist kein Bild, das ClipSound lesen kann (PNG, JPG, WebP, GIF …)."));
        }

        async Task ChooseAsync()
        {
            var files = Gtk.FileDialog.New();
            files.SetTitle("Bild für das ClipSound-Icon");
            var filter = Gtk.FileFilter.New();
            filter.SetName("Bilder");
            filter.AddPixbufFormats();
            var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
            filters.Append(filter);
            files.SetFilters(filters);
            files.SetDefaultFilter(filter);
            try
            {
                var file = await files.OpenAsync(parent.GetRoot() as Gtk.Window);
                if (file?.GetPath() is { } path) Use(path);
            }
            catch { /* abgebrochen */ }
        }

        var reset = Ui.IconButton("user-trash-symbolic", "Eigenes Icon entfernen", CustomIcon.Reset, "flat");
        var choose = Ui.Button("Bild auswählen …", () => _ = ChooseAsync());
        reset.SetValign(Gtk.Align.Center);
        choose.SetValign(Gtk.Align.Center);
        row.AddSuffix(reset);
        row.AddSuffix(choose);

        void Update()
        {
            row.SetTitle(CustomIcon.IsSet ? "Eigenes Icon" : "Standard-Icon");
            preview.SetFromIconName(CustomIcon.IconName);
            reset.SetVisible(CustomIcon.IsSet);
        }
        Update();
        CustomIcon.Changed += Update;
        dialog.OnClosed += (_, _) => CustomIcon.Changed -= Update;

        // Bild auf die Zeile ziehen
        var drop = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy);
        drop.OnDrop += (_, e) =>
        {
            var paths = MainWindow.DroppedPaths(e.Value);
            if (paths.Count == 0) return false;
            Use(paths[0]);
            return true;
        };
        row.AddController(drop);

        return Ui.Group("App-Icon",
            "Gilt für das Fenster und – wenn ClipSound mit install.sh installiert ist – für App-Menü und Dock. Bild hier hineinziehen geht auch.",
            row);
    }
}
