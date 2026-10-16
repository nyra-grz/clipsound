namespace ClipSound;

internal static class Program
{
    public const string AppId = "at.timur.ClipSound";

    public static int Main(string[] args)
    {
        // Nach einem Update startet die neue Version, während sich die alte noch beendet – kurz warten,
        // sonst würde sie die alte (gleich weg) bitten, sich zu zeigen, und selbst gleich wieder gehen
        if (args.Contains("--updated")) Thread.Sleep(1500);

        Adw.Module.Initialize();
        // Testläufe dürfen mehrere Instanzen haben; sonst holt ein zweiter Start nur das Fenster nach vorne
        var app = Adw.Application.New(AppId, MainWindow.TestMode ? Gio.ApplicationFlags.NonUnique : Gio.ApplicationFlags.DefaultFlags);
        MainWindow? window = null;
        app.OnStartup += (_, _) =>
        {
            Ui.InstallCss();
            // Symbole aus dem Programmordner: App-Symbol und Rückfall für Symbole, die das System-Thema nicht hat
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "icons");
            if (Gdk.Display.GetDefault() is { } display && Directory.Exists(assets))
                Gtk.IconTheme.GetForDisplay(display).AddSearchPath(assets);
            Gtk.Window.SetDefaultIconName("clipsound");
        };
        app.OnActivate += (_, _) =>
        {
            if (window is not null) { window.Present(); return; } // zweiter Start oder aus dem Hintergrund zurück
            window = new MainWindow(app);
            window.Present();
            window.Started();
        };
        // Eigene Startargumente nicht an GTK weitergeben (das kennt --library usw. nicht)
        return app.RunWithSynchronizationContext(null);
    }
}
