using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ClipSound;

/// <summary>Prüft GitHub auf eine neuere Version, lädt sie und tauscht die laufende exe aus.</summary>
public static class Updater
{
    public const string Repo = "nyra-grz/clipsound";
    public const string AssetName = "ClipSound-Windows-x64.zip";

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
        {
            if (asset.GetProperty("name").GetString() == AssetName)
                return new Release(version, asset.GetProperty("browser_download_url").GetString()!, root.GetProperty("html_url").GetString()!);
        }
        return null;
    }

    /// <summary>Lädt das Update, tauscht die exe aus und startet sie neu. Wirft bei Fehlern.</summary>
    public static async Task InstallAsync(Release release, IProgress<double>? progress = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
        var dir = Path.GetDirectoryName(exe)!;

        // Dürfen wir in den Ordner schreiben? (z. B. nicht in „Programme“ ohne Admin)
        var probe = Path.Combine(dir, ".clipsound-schreibtest");
        try { File.WriteAllText(probe, ""); File.Delete(probe); }
        catch { throw new InvalidOperationException($"Keine Schreibrechte für {dir}. Lade das Update bitte von GitHub."); }

        var work = Path.Combine(Path.GetTempPath(), "ClipSound-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, AssetName);

        using (var response = await Http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1, read = 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(zip);
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
        ZipFile.ExtractToDirectory(zip, extracted);
        var newExe = Path.Combine(extracted, "ClipSound.exe");
        if (!File.Exists(newExe)) throw new InvalidOperationException("Das heruntergeladene Update ist beschädigt.");

        // Eine laufende exe kann man nicht überschreiben, aber umbenennen
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe); // zurück auf die alte Version
            throw;
        }

        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Räumt die alte exe vom letzten Update weg (läuft im Hintergrund, die alte Instanz beendet sich evtl. noch).</summary>
    public static void CleanupOldVersion()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var old = exe + ".old";
        if (!File.Exists(old)) return;
        Task.Run(async () =>
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { await Task.Delay(500); }
            }
        });
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
