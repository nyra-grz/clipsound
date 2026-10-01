using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClipSound;

/// <summary>
/// Eine Taste oder Kombination. Gespeichert wird der virtuelle Tastencode,
/// damit globale Kürzel (RegisterHotKey) funktionieren.
/// </summary>
public sealed record KeyBind(int Vk, int Mods, string Label)
{
    // Werte wie bei RegisterHotKey
    public const int Alt = 0x1, Ctrl = 0x2, Shift = 0x4;

    /// <summary>
    /// Mit Umschalt + Strg oder Alt funktioniert die Kombination auch im Hintergrund.
    /// (Strg+Alt allein ist auf deutschen Tastaturen AltGr – damit würde man @, € usw. kaputt machen.)
    /// </summary>
    public bool IsGlobal => (Mods & Shift) != 0 && (Mods & (Ctrl | Alt)) != 0;

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

    public static int ModsFrom(ModifierKeys keys) =>
        (keys.HasFlag(ModifierKeys.Control) ? Ctrl : 0) |
        (keys.HasFlag(ModifierKeys.Alt) ? Alt : 0) |
        (keys.HasFlag(ModifierKeys.Shift) ? Shift : 0);

    public static KeyBind From(Key key, ModifierKeys modifiers)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return new KeyBind(vk, ModsFrom(modifiers), NameFor(vk, key));
    }

    public bool Matches(Key key, ModifierKeys modifiers) =>
        KeyInterop.VirtualKeyFromKey(key) == Vk && ModsFrom(modifiers) == Mods;

    /// <summary>Standardtasten: 1–0, dann Q W E R T Z U I O P</summary>
    public static readonly int[] Defaults =
        { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30, 'Q', 'W', 'E', 'R', 'T', 'Z', 'U', 'I', 'O', 'P' };

    public static KeyBind Default(int vk) => new(vk, 0, NameFor(vk, KeyInterop.KeyFromVirtualKey(vk)));

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>Beschriftung der Taste im aktuellen Tastaturlayout</summary>
    private static string NameFor(int vk, Key key)
    {
        switch (key)
        {
            case Key.Space: return "Leertaste";
            case Key.Enter: return "Enter";
            case Key.Tab: return "Tab";
            case Key.Left: return "←";
            case Key.Right: return "→";
            case Key.Up: return "↑";
            case Key.Down: return "↓";
            case Key.Home: return "Pos1";
            case Key.End: return "Ende";
            case Key.PageUp: return "Bild↑";
            case Key.PageDown: return "Bild↓";
            case Key.Insert: return "Einfg";
            case >= Key.F1 and <= Key.F24: return key.ToString();
            case >= Key.NumPad0 and <= Key.NumPad9: return "Num " + (key - Key.NumPad0);
            case Key.Multiply: return "Num *";
            case Key.Add: return "Num +";
            case Key.Subtract: return "Num -";
            case Key.Divide: return "Num /";
            case Key.Decimal: return "Num ,";
        }
        try
        {
            uint ch = MapVirtualKey((uint)vk, 2) & 0x7FFF; // MAPVK_VK_TO_CHAR
            if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
        }
        catch { /* nicht unter Windows */ }
        return key.ToString();
    }
}

/// <summary>Speichert, welcher Sound welche Taste hat (keybinds.json neben dem Sound-Ordner).</summary>
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
        var free = new Queue<int>(KeyBind.Defaults.Where(vk => !next.Values.Any(b => b.Vk == vk && b.Mods == 0)));
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
            previous = Binds.FirstOrDefault(b => b.Key != id && b.Value.Vk == bind.Vk && b.Value.Mods == bind.Mods).Key;
            if (previous is not null)
            {
                Binds.Remove(previous);
                _unbound.Add(previous);
            }
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

    public string? SoundFor(Key key, ModifierKeys modifiers) =>
        Binds.FirstOrDefault(b => b.Value.Matches(key, modifiers)).Key;

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new Saved(Binds, _unbound.OrderBy(x => x).ToList()),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }
        catch { /* egal */ }
    }
}

/// <summary>Systemweite Kürzel über RegisterHotKey.</summary>
public sealed class GlobalHotKeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, string> _ids = new();
    public HashSet<string> Active { get; } = new();
    public HashSet<string> Failed { get; } = new();
    public event Action<string>? Pressed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotKeys(IntPtr hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    public void Register(Dictionary<string, KeyBind> binds)
    {
        UnregisterAll();
        int next = 1;
        foreach (var (soundId, bind) in binds.OrderBy(b => b.Key))
        {
            if (!bind.IsGlobal) continue;
            if (RegisterHotKey(_hwnd, next, (uint)bind.Mods | MOD_NOREPEAT, (uint)bind.Vk))
            {
                _ids[next] = soundId;
                Active.Add(soundId);
            }
            else
            {
                Failed.Add(soundId); // eine andere App nutzt die Kombination schon
            }
            next++;
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_hwnd, id);
        _ids.Clear();
        Active.Clear();
        Failed.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _ids.TryGetValue(wParam.ToInt32(), out var soundId))
        {
            Pressed?.Invoke(soundId);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => UnregisterAll();
}
