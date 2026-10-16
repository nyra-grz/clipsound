namespace ClipSound;

internal static class Program
{
    public const string AppId = "at.timur.ClipSound";

    public static int Main(string[] args)
    {
        // Nach einem Update startet die neue Version, während sich die alte noch beendet – kurz warten,
        // sonst würde sie die alte (gleich weg) bitten, sich zu zeigen, und selbst gleich wieder gehen
        if (args.Contains("--updated")) Thread.Sleep(1500);

        try { Adw.Module.Initialize(); }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            return TooOld("ClipSound braucht GTK 4 und libadwaita – die fehlen hier noch.");
        }
        // Adw.Dialog & Co. gibt es erst ab libadwaita 1.5 (GTK 4.14)
        if ((Adw.Functions.GetMajorVersion() == 1 && Adw.Functions.GetMinorVersion() < 5) || Gtk.Functions.GetMinorVersion() < 14)
            return TooOld($"ClipSound braucht libadwaita 1.5 oder neuer (hier: {Adw.Functions.GetMajorVersion()}.{Adw.Functions.GetMinorVersion()}).");

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

    /// <summary>Ohne passendes GTK kein Fenster – Hinweis im Terminal und als Systembenachrichtigung</summary>
    private static int TooOld(string problem)
    {
        const string hint = "Ubuntu/Debian/Mint: sudo apt install libadwaita-1-0 · Fedora: sudo dnf install libadwaita · Arch: sudo pacman -S libadwaita. "
                          + "Ältere Systeme (z. B. Ubuntu 22.04) bitte auf eine neuere Version aktualisieren.";
        Console.Error.WriteLine(problem);
        Console.Error.WriteLine(hint);
        try { System.Diagnostics.Process.Start("notify-send", new[] { "-i", "dialog-error", "ClipSound", problem + "\n" + hint })?.WaitForExit(3000); } catch { }
        return 1;
    }
}
