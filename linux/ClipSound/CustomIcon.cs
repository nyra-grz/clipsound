using System.Security.Cryptography;

namespace ClipSound;

/// <summary>
/// Eigenes App-Icon (wie in der Mac- und Windows-App): liegt als PNG in ~/.config/ClipSound und gilt fürs Fenster,
/// den Info-Dialog und – wenn ClipSound mit install.sh installiert ist – fürs App-Menü und das Dock.
/// </summary>
public static class CustomIcon
{
    public const string DefaultName = "clipsound";
    private const int Size = 256;

    /// <summary>~/.config/ClipSound, beim Testen mit --library der Ordner daneben (wie keybinds.json)</summary>
    private static string _folder = SoundLibrary.SupportFolder;
    private static string FilePath => Path.Combine(_folder, "AppIcon.png");
    /// <summary>GTK findet Fenster-Symbole nur über Namen – darum eine Kopie in einem eigenen Suchordner</summary>
    private static string IconFolder => Path.Combine(_folder, "icons");
    private static string DesktopFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "applications", Program.AppId + ".desktop");

    public static bool IsSet => File.Exists(FilePath);

    /// <summary>Name fürs Symbol-Thema. Ändert sich mit jedem neuen Bild, sonst zeigt GTK das alte aus dem Zwischenspeicher.</summary>
    public static string IconName { get; private set; } = DefaultName;

    /// <summary>Neues Bild oder zurückgesetzt – Fenster und Dialoge sollen ihr Symbol neu setzen</summary>
    public static event Action? Changed;

    /// <summary>Beim Start: Suchordner anmelden, Kopie und App-Menü-Eintrag abgleichen (install.sh/Updates setzen ihn zurück)</summary>
    public static void Startup(Gdk.Display display, string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(IconFolder);
        Gtk.IconTheme.GetForDisplay(display).AddSearchPath(IconFolder);
        Sync(display, notify: false);
    }

    /// <summary>Mittig quadratisch zuschneiden, auf 256 px bringen und speichern. false = kein lesbares Bild.</summary>
    public static bool Set(string source)
    {
        try
        {
            var image = GdkPixbuf.Pixbuf.NewFromFile(source) ?? throw new InvalidOperationException();
            image = image.ApplyEmbeddedOrientation() ?? image; // Handyfotos stehen sonst evtl. auf der Seite
            int w = image.GetWidth(), h = image.GetHeight(), side = Math.Min(w, h);
            var square = image.NewSubpixbuf((w - side) / 2, (h - side) / 2, side, side);
            var scaled = square.ScaleSimple(Size, Size, GdkPixbuf.InterpType.Hyper) ?? throw new InvalidOperationException();
            var temp = FilePath + ".neu";
            Gdk.Texture.NewForPixbuf(scaled).SaveToPng(temp);
            File.Move(temp, FilePath, true);
        }
        catch { return false; }
        Sync(Gdk.Display.GetDefault(), notify: true);
        return true;
    }

    public static void Reset()
    {
        try { File.Delete(FilePath); } catch { /* egal */ }
        Sync(Gdk.Display.GetDefault(), notify: true);
    }

    /// <summary>Bild für die Vorschau in den Einstellungen (eigenes Bild oder das Standard-Symbol)</summary>
    public static Gtk.Image Preview(int pixelSize)
    {
        var image = Gtk.Image.NewFromIconName(IconName);
        image.SetPixelSize(pixelSize);
        return image;
    }

    private static void Sync(Gdk.Display? display, bool notify)
    {
        string? copy = null;
        if (IsSet)
        {
            try
            {
                var bytes = File.ReadAllBytes(FilePath);
                var name = "clipsound-eigen-" + Convert.ToHexString(SHA256.HashData(bytes))[..10].ToLowerInvariant();
                copy = Path.Combine(IconFolder, name + ".png");
                Directory.CreateDirectory(IconFolder);
                if (!File.Exists(copy)) File.WriteAllBytes(copy, bytes);
                IconName = name;
            }
            catch { copy = null; IconName = DefaultName; }
        }
        else IconName = DefaultName;

        // Alte Kopien weg, damit sich nichts ansammelt
        try
        {
            foreach (var old in Directory.EnumerateFiles(IconFolder, "clipsound-eigen-*.png"))
                if (old != copy) File.Delete(old);
        }
        catch { /* egal */ }

        UpdateDesktopFile(copy);
        if (display is not null)
        {
            // Suchpfad neu setzen = GTK liest die Ordner sofort neu ein (sonst erst nach ein paar Sekunden)
            var theme = Gtk.IconTheme.GetForDisplay(display);
            theme.SetSearchPath(theme.GetSearchPath() ?? [IconFolder]);
        }
        Gtk.Window.SetDefaultIconName(IconName);
        if (notify) Changed?.Invoke();
    }

    /// <summary>App-Menü und Dock nehmen das Symbol aus dem .desktop-Eintrag von install.sh – den gibt es nur nach der Installation</summary>
    private static void UpdateDesktopFile(string? iconPath)
    {
        // Testläufe mit --library lassen den echten Eintrag in Ruhe (außer XDG_DATA_HOME zeigt woanders hin)
        if (_folder != SoundLibrary.SupportFolder && Environment.GetEnvironmentVariable("XDG_DATA_HOME") is null) return;
        try
        {
            if (!File.Exists(DesktopFile)) return;
            var lines = File.ReadAllLines(DesktopFile);
            var wanted = "Icon=" + (iconPath ?? DefaultName);
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Icon=") || lines[i] == wanted) continue;
                lines[i] = wanted;
                changed = true;
            }
            if (!changed) return;
            var temp = DesktopFile + ".neu";
            File.WriteAllLines(temp, lines);
            File.Move(temp, DesktopFile, true); // in einem Rutsch, damit das App-Menü nie eine halbe Datei sieht
        }
        catch { /* ohne Eintrag bleibt es beim Fenster-Symbol */ }
    }
}
