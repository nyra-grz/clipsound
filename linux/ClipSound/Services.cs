using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipSound;

/// <summary>Lautstärke der echten Lautsprecher (Standard-Ausgabe), 0…1 – über pactl (PulseAudio/PipeWire) oder wpctl (PipeWire).</summary>
public static class SystemVolume
{
    public static double? Get()
    {
        if (Run("pactl", "get-sink-volume @DEFAULT_SINK@") is { } p && Regex.Match(p, @"(\d+)%") is { Success: true } m)
            return Math.Clamp(int.Parse(m.Groups[1].Value) / 100.0, 0, 1);
        if (Run("wpctl", "get-volume @DEFAULT_AUDIO_SINK@") is { } w && Regex.Match(w, @"Volume:\s*([0-9.]+)") is { Success: true } v
            && double.TryParse(v.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level))
            return Math.Clamp(level, 0, 1);
        return null;
    }

    public static bool Set(double level)
    {
        int pct = (int)Math.Round(Math.Clamp(level, 0, 1) * 100);
        if (Run("pactl", $"set-sink-volume @DEFAULT_SINK@ {pct}%") is not null)
        {
            if (pct > 0) Run("pactl", "set-sink-mute @DEFAULT_SINK@ 0"); // wer lauter gestellt wird, soll auch was hören
            return true;
        }
        if (Run("wpctl", $"set-volume @DEFAULT_AUDIO_SINK@ {pct / 100.0:0.00}".Replace(',', '.')) is not null)
        {
            if (pct > 0) Run("wpctl", "set-mute @DEFAULT_AUDIO_SINK@ 0");
            return true;
        }
        return false;
    }

    /// <summary>Befehl ausführen, Ausgabe zurück (null = gibt es nicht / Fehler)</summary>
    private static string? Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(1500)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}

/// <summary>Prüft GitHub auf eine neuere Version, lädt sie und tauscht die laufende Datei aus.</summary>
public static class Updater
{
    public const string Repo = "nyra-grz/clipsound";
    public static string AssetName => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ClipSound-Linux-arm64.tar.gz" : "ClipSound-Linux-x64.tar.gz";

    public sealed record Release(string Version, string Download, string Page);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ClipSound-Updater"); // GitHub verlangt einen User-Agent
        return c;
    }

    /// <summary>--pretend-version tut so, als wäre eine ältere Version installiert (zum Testen)</summary>
    public static string CurrentVersion
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--pretend-version");
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
    }

    public static async Task<Release?> CheckAsync()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
        if (!IsNewer(version, CurrentVersion)) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == AssetName)
                return new Release(version, asset.GetProperty("browser_download_url").GetString()!, root.GetProperty("html_url").GetString()!);
        return null;
    }

    /// <summary>Lädt das Update, tauscht die Datei aus und startet sie neu. Wirft bei Fehlern.</summary>
    public static async Task InstallAsync(Release release, IProgress<double>? progress, Action quit)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
        var dir = Path.GetDirectoryName(exe)!;
        var probe = Path.Combine(dir, ".clipsound-schreibtest");
        try { File.WriteAllText(probe, ""); File.Delete(probe); }
        catch { throw new InvalidOperationException($"Keine Schreibrechte für {dir}. Lade das Update bitte von GitHub oder installiere ClipSound mit install.sh."); }

        var work = Path.Combine(Path.GetTempPath(), "ClipSound-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var archive = Path.Combine(work, AssetName);
        using (var response = await Http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1, read = 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(archive);
            var buffer = new byte[81920];
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        var extracted = Path.Combine(work, "neu");
        Directory.CreateDirectory(extracted);
        await using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
            await TarFile.ExtractToDirectoryAsync(gz, extracted, overwriteFiles: true);
        var newExe = Directory.EnumerateFiles(extracted, "ClipSound", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("Das heruntergeladene Update ist beschädigt.");

        // Symbole usw. mitnehmen: erst daneben auspacken, dann in einem Rutsch tauschen
        var newAssets = Path.Combine(Path.GetDirectoryName(newExe)!, "Assets");
        if (Directory.Exists(newAssets))
        {
            string assets = Path.Combine(dir, "Assets"), stagedAssets = assets + ".neu", oldAssets = assets + ".alt";
            DeleteDirectory(stagedAssets);
            DeleteDirectory(oldAssets);
            CopyDirectory(newAssets, stagedAssets);
            if (Directory.Exists(assets)) Directory.Move(assets, oldAssets);
            Directory.Move(stagedAssets, assets);
            DeleteDirectory(oldAssets);
        }

        // Unter Linux darf man eine laufende Datei ersetzen – sie bleibt bis zum Beenden im Speicher
        var staged = exe + ".neu";
        File.Copy(newExe, staged, true);
        File.SetUnixFileMode(staged, File.GetUnixFileMode(newExe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        File.Move(staged, exe, true);
        DeleteDirectory(work);
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
        quit();
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var sub in Directory.EnumerateDirectories(from)) CopyDirectory(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    public static bool IsNewer(string a, string b)
    {
        int[] Parse(string s) => s.Split('.').Select(p => int.TryParse(p, out var x) ? x : 0).ToArray();
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}

/// <summary>Lautstärke, Überlappen, Name … (~/.config/ClipSound/settings.json)</summary>
public sealed class Settings
{
    public double Volume { get; set; } = 0.8;
    public bool Overlap { get; set; }
    public string? LobbyName { get; set; }
    /// <summary>X = nur Fenster zu, ClipSound läuft mit Symbol im Infobereich weiter (Tastenkürzel gehen weiter)</summary>
    public bool KeepInBackground { get; set; }

    private static string FilePath => Path.Combine(SoundLibrary.SupportFolder, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* egal */ }
    }
}

/// <summary>Links im Browser, Ordner im Dateimanager öffnen (xdg-open gibt es auf allen Desktops)</summary>
public static class Open
{
    public static void Url(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
