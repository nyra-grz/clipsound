namespace ClipSound;

/// <summary>Einstellungen als libadwaita-Einstellungsfenster: Fenster, Sounds/Ton, Tastenkürzel, Datenschutz.</summary>
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
}
