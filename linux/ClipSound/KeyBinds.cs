using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClipSound;

/// <summary>
/// Eine Taste oder Kombination. Gespeichert wird der GDK-Tastenwert (Keyval) – der ist gleich dem X11-Keysym,
/// damit gehen auch die systemweiten Kürzel ohne Umrechnen.
/// </summary>
public sealed record KeyBind(uint Keyval, int Mods, string Label)
{
    // Wie unter Windows gespeichert
    public const int Alt = 0x1, Ctrl = 0x2, Shift = 0x4;

    /// <summary>
    /// Mit Alt (z. B. Alt+2) oder Strg+Umschalt geht die Kombination überall, auch im Hintergrund (unter X11).
    /// Strg allein bleibt lokal, sonst wären Strg+C/V usw. in allen Apps weg. Strg+Alt ist AltGr (@, € …).
    /// </summary>
    public bool IsGlobal
    {
        get
        {
            bool alt = (Mods & Alt) != 0, ctrl = (Mods & Ctrl) != 0, shift = (Mods & Shift) != 0;
            if (alt && ctrl && !shift) return false;
            return alt || (ctrl && shift);
        }
    }

    public string Display
    {
        get
        {
            var sb = new StringBuilder();
            if ((Mods & Ctrl) != 0) sb.Append("Strg+");
            if ((Mods & Alt) != 0) sb.Append("Alt+");
            if ((Mods & Shift) != 0) sb.Append("Umschalt+");
            return sb.Append(Label).ToString();
        }
    }

    public static int ModsFrom(Gdk.ModifierType state) =>
        (state.HasFlag(Gdk.ModifierType.ControlMask) ? Ctrl : 0) | (state.HasFlag(Gdk.ModifierType.AltMask) ? Alt : 0) | (state.HasFlag(Gdk.ModifierType.ShiftMask) ? Shift : 0);

    /// <summary>Kleinbuchstaben speichern, damit Umschalt+K und K dieselbe Taste meinen</summary>
    public static uint Normalize(uint keyval) => Gdk.Functions.KeyvalToLower(keyval);

    public static KeyBind From(uint keyval, Gdk.ModifierType state) => new(Normalize(keyval), ModsFrom(state), NameFor(Normalize(keyval)));

    public bool Matches(uint keyval, Gdk.ModifierType state) => Normalize(keyval) == Keyval && ModsFrom(state) == Mods;

    /// <summary>Standardtasten: 1–0, dann Q W E R T Z U I O P</summary>
    public static readonly uint[] Defaults = "1234567890qwertzuiop".Select(c => (uint)c).ToArray();

    public static KeyBind Default(uint keyval) => new(keyval, 0, NameFor(keyval));

    /// <summary>Beschriftung der Taste</summary>
    public static string NameFor(uint keyval) => keyval switch
    {
        0x20 => "Leertaste", 0xff0d => "Enter", 0xff09 => "Tab",
        0xff51 => "←", 0xff52 => "↑", 0xff53 => "→", 0xff54 => "↓",
        0xff50 => "Pos1", 0xff57 => "Ende", 0xff55 => "Bild↑", 0xff56 => "Bild↓", 0xff63 => "Einfg",
        >= 0xffb0 and <= 0xffb9 => "Num " + (keyval - 0xffb0),
        0xffaa => "Num *", 0xffab => "Num +", 0xffad => "Num -", 0xffaf => "Num /", 0xffae => "Num ,",
        >= 0xffbe and <= 0xffd5 => "F" + (keyval - 0xffbe + 1),
        _ => Gdk.Functions.KeyvalToUnicode(keyval) is var u and > 32 ? char.ConvertFromUtf32((int)u).ToUpperInvariant() : Gdk.Functions.KeyvalName(keyval) ?? "?",
    };
}

/// <summary>Speichert, welcher Sound welche Taste hat (keybinds.json in ~/.config/ClipSound).</summary>
public sealed class KeyBindStore
{
    private readonly string _file;
    public Dictionary<string, KeyBind> Binds { get; private set; } = new();
    /// <summary>Sounds, deren Taste bewusst entfernt wurde – bekommen keine Standardtaste mehr</summary>
    private HashSet<string> _unbound = new();

    private sealed record Saved(Dictionary<string, KeyBind> Binds, List<string> Unbound);

    public KeyBindStore(string file)
    {
        _file = file;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file));
            if (saved is not null)
            {
                Binds = saved.Binds ?? new();
                _unbound = new HashSet<string>(saved.Unbound ?? new());
            }
        }
        catch { /* noch keine Datei */ }
    }

    /// <summary>Neue Sounds bekommen die nächste freie Standardtaste, gelöschte verlieren ihre.</summary>
    public void Sync(IReadOnlyList<Sound> sounds)
    {
        var ids = sounds.Select(s => s.FileName).ToHashSet();
        var next = Binds.Where(b => ids.Contains(b.Key)).ToDictionary(b => b.Key, b => b.Value);
        _unbound.IntersectWith(ids);
        var free = new Queue<uint>(KeyBind.Defaults.Where(k => !next.Values.Any(b => b.Keyval == k && b.Mods == 0)));
        foreach (var s in sounds)
        {
            if (free.Count == 0) break;
            if (!next.ContainsKey(s.FileName) && !_unbound.Contains(s.FileName))
                next[s.FileName] = KeyBind.Default(free.Dequeue());
        }
        bool changed = next.Count != Binds.Count || next.Any(b => !Binds.TryGetValue(b.Key, out var old) || old != b.Value);
        Binds = next;
        if (changed) Save();
    }

    /// <summary>Setzt die Taste. Gibt die ID des Sounds zurück, der sie vorher hatte.</summary>
    public string? Set(string id, KeyBind? bind)
    {
        string? previous = null;
        if (bind is not null)
        {
            previous = Binds.FirstOrDefault(b => b.Key != id && b.Value.Keyval == bind.Keyval && b.Value.Mods == bind.Mods).Key;
            if (previous is not null) { Binds.Remove(previous); _unbound.Add(previous); }
            Binds[id] = bind;
            _unbound.Remove(id);
        }
        else
        {
            Binds.Remove(id);
            _unbound.Add(id);
        }
        Save();
        return previous;
    }

    public string? SoundFor(uint keyval, Gdk.ModifierType state) => Binds.FirstOrDefault(b => b.Value.Matches(keyval, state)).Key;

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(new Saved(Binds, _unbound.OrderBy(x => x).ToList()), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* egal */ }
    }
}

/// <summary>
/// Systemweite Tastenkürzel über X11 (XGrabKey) – funktionieren auch, wenn ClipSound im Hintergrund ist.
/// Unter Wayland lassen sich Tasten nicht systemweit abgreifen; dann gelten die Kürzel nur im Fenster.
/// Alle Xlib-Aufrufe laufen auf einem eigenen Thread mit eigener Verbindung.
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    public HashSet<string> Active { get; } = new();
    public HashSet<string> Failed { get; } = new();
    public event Action<string>? Pressed;
    /// <summary>X11 da und keine reine Wayland-Sitzung</summary>
    public bool Supported { get; }
    public static bool IsWayland => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private const int KeyPress = 2;
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16;
    private IntPtr _display, _root;
    private readonly object _lock = new();
    private Dictionary<string, KeyBind> _wanted = new();
    private bool _dirty;
    private readonly Dictionary<(int code, uint mods), string> _grabbed = new();
    private volatile bool _stop;
    private static int _errors;
    private static XErrorHandler? _handler; // muss leben bleiben, sonst sammelt der GC ihn ein

    private delegate int XErrorHandler(IntPtr display, IntPtr error);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, byte[] ev);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, bool discard);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    public GlobalHotKeys()
    {
        if (!OperatingSystem.IsLinux() || IsWayland || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        try
        {
            // Eine schon belegte Kombination liefert BadAccess – ohne eigenen Handler würde Xlib das Programm beenden
            _handler ??= (_, _) => { Interlocked.Increment(ref _errors); return 0; };
            XSetErrorHandler(_handler);
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return;
            _root = XDefaultRootWindow(_display);
            Supported = true;
            new Thread(Loop) { IsBackground = true, Name = "ClipSound Tastenkürzel" }.Start();
        }
        catch { Supported = false; }
    }

    public void Register(Dictionary<string, KeyBind> binds)
    {
        lock (_lock)
        {
            _wanted = binds.Where(b => b.Value.IsGlobal).ToDictionary(b => b.Key, b => b.Value);
            _dirty = true;
            if (!Supported) { Active.Clear(); Failed.Clear(); }
        }
        // kurz warten, damit Active/Failed stimmen, wenn der Aufrufer gleich danach nachsieht
        if (Supported) for (int i = 0; i < 20 && Dirty; i++) Thread.Sleep(5);
    }

    public void UnregisterAll() => Register(new());

    private bool Dirty { get { lock (_lock) return _dirty; } }

    private void Loop()
    {
        var ev = new byte[192]; // XEvent
        while (!_stop)
        {
            bool dirty;
            Dictionary<string, KeyBind> wanted;
            lock (_lock) { dirty = _dirty; wanted = _wanted; _dirty = false; }
            if (dirty) Apply(wanted);
            while (XPending(_display) > 0)
            {
                XNextEvent(_display, ev);
                if (BitConverter.ToInt32(ev, 0) != KeyPress) continue;
                uint state = BitConverter.ToUInt32(ev, 80) & (ShiftMask | ControlMask | Mod1Mask);
                int code = (int)BitConverter.ToUInt32(ev, 84);
                if (_grabbed.TryGetValue((code, state), out var id)) Pressed?.Invoke(id);
            }
            Thread.Sleep(15);
        }
    }

    private void Apply(Dictionary<string, KeyBind> wanted)
    {
        foreach (var ((code, mods), _) in _grabbed)
            foreach (var extra in IgnoredMods) XUngrabKey(_display, code, mods | extra, _root);
        _grabbed.Clear();
        var active = new HashSet<string>();
        var failed = new HashSet<string>();
        foreach (var (id, bind) in wanted.OrderBy(b => b.Key))
        {
            int code = XKeysymToKeycode(_display, (IntPtr)bind.Keyval); // GDK-Keyval = X11-Keysym
            if (code == 0) { failed.Add(id); continue; }
            uint mods = ((bind.Mods & KeyBind.Shift) != 0 ? ShiftMask : 0) | ((bind.Mods & KeyBind.Ctrl) != 0 ? ControlMask : 0) | ((bind.Mods & KeyBind.Alt) != 0 ? Mod1Mask : 0);
            int before = Volatile.Read(ref _errors);
            foreach (var extra in IgnoredMods) XGrabKey(_display, code, mods | extra, _root, false, 1, 1); // GrabModeAsync
            XSync(_display, false);
            if (Volatile.Read(ref _errors) != before)
            {
                foreach (var extra in IgnoredMods) XUngrabKey(_display, code, mods | extra, _root);
                failed.Add(id); // eine andere App nutzt die Kombination schon
                continue;
            }
            _grabbed[(code, mods)] = id;
            active.Add(id);
        }
        lock (_lock) { Active.Clear(); Active.UnionWith(active); Failed.Clear(); Failed.UnionWith(failed); }
    }

    /// <summary>Feststell- und NumLock dürfen die Kombination nicht verhindern</summary>
    private static readonly uint[] IgnoredMods = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };

    public void Dispose()
    {
        _stop = true;
        // Die Verbindung räumt das Betriebssystem beim Beenden ab (und damit alle Griffe)
    }
}
