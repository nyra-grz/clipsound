using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClipSound;

public sealed record Sound(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Anzeigename: ohne Endung, Bindestriche/Unterstriche als Leerzeichen</summary>
    public string Title => Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(Path), "[-_]+", " ");
}

/// <summary>
/// Der Sound-Ordner unter Linux: ~/Musik/ClipSound (bzw. der XDG-Musik-Ordner), startet leer.
/// Einstellungen liegen in ~/.config/ClipSound.
/// </summary>
public sealed class SoundLibrary
{
    /// <summary>MP3, WAV, OGG und AIFF spielt ClipSound selbst; die anderen mit ffmpeg, wenn es installiert ist.</summary>
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".aif", ".aiff", ".flac", ".m4a", ".aac", ".opus", ".webm", ".caf", ".wma",
    };

    public string Folder { get; }
    public List<Sound> Sounds { get; private set; } = new();
    /// <summary>Die Liste hat sich geändert (Import, Löschen, neu eingelesen)</summary>
    public event Action? Changed;

    /// <summary>~/.config/ClipSound (XDG_CONFIG_HOME)</summary>
    public static string SupportFolder { get; } = CreateSupportFolder();

    private static string CreateSupportFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipSound");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Musik-Ordner aus den XDG-Benutzerordnern (deutsch meist ~/Musik), sonst ~/ClipSound</summary>
    public static string DefaultFolder
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(music) || music == home ? Path.Combine(home, "ClipSound") : Path.Combine(music, "ClipSound");
        }
    }

    /// <summary>Hier liegen keybinds.json und settings.json (nicht im Sound-Ordner).</summary>
    public string SettingsFolder { get; }

    /// <param name="folder">Anderer Sound-Ordner (nur zum Testen)</param>
    public SoundLibrary(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
        SettingsFolder = folder is null ? SupportFolder : Path.GetDirectoryName(Path.GetFullPath(folder))!;
        Directory.CreateDirectory(Folder);
        Reload();
    }

    public void Reload()
    {
        // Neueste zuerst, bei gleichem Datum alphabetisch
        Sounds = Directory.EnumerateFiles(Folder)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith('.'))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Sound(f.FullName))
            .ToList();
        Changed?.Invoke();
    }

    /// <summary>Importiert Dateien und ganze Ordner. Nur Dateien, die sich als Audio öffnen lassen, werden übernommen.</summary>
    public (int Added, List<string> Rejected) Import(IEnumerable<string> paths)
    {
        int added = 0;
        var rejected = new List<string>();
        foreach (var file in Expand(paths))
        {
            var problem = AudioExtensions.Contains(Path.GetExtension(file)) ? SoundPlayer.CheckDecodable(file) : "kein Audioformat";
            if (problem is not null)
            {
                rejected.Add($"{Path.GetFileName(file)} ({problem})");
                continue;
            }
            try
            {
                var dest = UniqueDestination(file);
                File.Copy(file, dest);
                File.SetCreationTimeUtc(dest, DateTime.UtcNow);
                File.SetLastWriteTimeUtc(dest, DateTime.UtcNow); // Linux kennt oft kein Erstelldatum – dann zählt das hier
                added++;
            }
            catch
            {
                rejected.Add(Path.GetFileName(file));
            }
        }
        Reload();
        return (added, rejected);
    }

    /// <summary>In den Papierkorb (freedesktop-Standard, wie im Dateimanager), sonst löschen.</summary>
    public void Delete(Sound sound)
    {
        if (!Trash.Move(sound.Path)) try { File.Delete(sound.Path); } catch { }
        Reload();
    }

    /// <summary>Sound-Ordner im Dateimanager öffnen</summary>
    public void Reveal()
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", $"\"{Folder}\"") { UseShellExecute = false }); } catch { }
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                // In Ordnern werden andere Dateien (Bilder, Text …) einfach übersprungen
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                             .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                    yield return f;
            }
            else if (File.Exists(p))
            {
                yield return p;
            }
        }
    }

    private string UniqueDestination(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var dest = Path.Combine(Folder, name + ext);
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(Folder, $"{name} ({i}){ext}");
        return dest;
    }
}

/// <summary>Papierkorb nach freedesktop.org-Spezifikation (~/.local/share/Trash) – den nutzen GNOME, KDE, Xfce …</summary>
public static class Trash
{
    public static bool Move(string path)
    {
        try
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            var files = Path.Combine(dataHome, "Trash", "files");
            var info = Path.Combine(dataHome, "Trash", "info");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);
            var name = Path.GetFileName(path);
            var target = name;
            for (int i = 2; File.Exists(Path.Combine(files, target)) || File.Exists(Path.Combine(info, target + ".trashinfo")); i++)
                target = $"{Path.GetFileNameWithoutExtension(name)}.{i}{Path.GetExtension(name)}";
            var full = Path.GetFullPath(path);
            var encoded = string.Join("/", full.Split('/').Select(Uri.EscapeDataString));
            File.WriteAllText(Path.Combine(info, target + ".trashinfo"),
                $"[Trash Info]\nPath={encoded}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
            File.Move(full, Path.Combine(files, target));
            return true;
        }
        catch { return false; }
    }
}
