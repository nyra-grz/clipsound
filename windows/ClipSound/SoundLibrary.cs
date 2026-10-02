using System.IO;
using System.Text.RegularExpressions;

namespace ClipSound;

public sealed record Sound(string Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Anzeigename: ohne Endung, Bindestriche/Unterstriche als Leerzeichen</summary>
    public string Title => Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(Path), "[-_]+", " ");
}

/// <summary>Der Sound-Ordner der App unter %AppData%\ClipSound\Sounds (startet leer).</summary>
public sealed class SoundLibrary
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".flac", ".aif", ".aiff",
    };

    public string Folder { get; }
    public List<Sound> Sounds { get; private set; } = new();

    /// <summary>%AppData%\ClipSound – übernimmt einmalig den alten „Meme Soundboard“-Ordner.</summary>
    public static string SupportFolder { get; } = CreateSupportFolder();

    private static string CreateSupportFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ClipSound");
        var old = Path.Combine(appData, "Meme Soundboard");
        try
        {
            if (!Directory.Exists(folder) && Directory.Exists(old)) Directory.Move(old, folder);
        }
        catch { /* dann eben neu anfangen */ }
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <param name="folder">Anderer Sound-Ordner (nur zum Testen)</param>
    public SoundLibrary(string? folder = null)
    {
        Folder = folder ?? Path.Combine(SupportFolder, "Sounds");
        Directory.CreateDirectory(Folder);
        Reload();
    }

    public void Reload()
    {
        // Neueste zuerst, bei gleichem Datum alphabetisch
        Sounds = Directory.EnumerateFiles(Folder)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new Sound(f.FullName))
            .ToList();
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

    public void Delete(Sound sound)
    {
        try
        {
            // In den Papierkorb statt endgültig löschen
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(sound.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
            File.Delete(sound.Path);
        }
        Reload();
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
