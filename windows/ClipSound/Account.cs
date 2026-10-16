using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClipSound;

// Konto + Clans gegen den ClipSound-Server (cloud/clans.js) – gleiche API wie Mac/iOS.
// Solo-Modus läuft weiter über Lobby.cs, ganz ohne Konto.
// Clan-Sounds liegen nicht auf dem Server, sondern auf den Geräten der Mitglieder (siehe Share.cs).

public sealed record AccountUser(string Id, string Email, string Name);
public sealed record ClanSummary(string Id, string Name, string Role, int Members) { public bool IsAdmin => Role == "admin"; }
/// <param name="Online">ClipSound ist bei der Person gerade offen</param>
public sealed record ClanMember(string Id, string Name, string Role, bool Owner = false, bool Online = false, bool InSession = false)
{
    public bool IsAdmin => Role == "admin";
}
/// <param name="Available">Gerade ist jemand online, der ihn auf dem Gerät hat</param>
/// <param name="By">Wer ihn geteilt hat</param>
public sealed record ClanSound(string Id, string Name, long Size, bool Available = true, string? By = null)
{
    public string Title => Regex.Replace(Path.GetFileNameWithoutExtension(Name), "[-_]+", " ");
}

public sealed class Account
{
    public static readonly Uri Server = ResolveServer();
    private static Uri ResolveServer()
    {
        var env = Environment.GetEnvironmentVariable("CLIPSOUND_SERVER");
        return string.IsNullOrWhiteSpace(env)
            ? new Uri("https://rooms-production-8e28.up.railway.app/")
            : new Uri(env.TrimEnd('/') + "/");
    }

    /// <summary>Datenschutzerklärung (gilt für alle Versionen)</summary>
    public static Uri PrivacyUrl => new(Server, "datenschutz");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _tokenFile;
    /// <summary>Gibt Clan-Sounds aus der eigenen Bibliothek an andere Mitglieder weiter, solange man angemeldet ist.</summary>
    public ClanShare Share { get; } = new(Server);

    private string? _token;
    public string? Token
    {
        get => _token;
        private set
        {
            _token = value;
            if (string.IsNullOrEmpty(value)) Share.Stop(); else Share.Start(value);
        }
    }
    public AccountUser? User { get; private set; }
    public List<ClanSummary> Clans { get; private set; } = new();
    public bool LoggedIn => User is not null;
    public string? Problem { get; set; }
    public bool Busy { get; private set; }
    public event Action? Changed;

    public Account(string tokenFile)
    {
        _tokenFile = tokenFile;
        try { if (File.Exists(_tokenFile)) Token = File.ReadAllText(_tokenFile).Trim(); } catch { }
        if (!string.IsNullOrEmpty(Token)) _ = RefreshMeAsync();
    }

    /// <summary>Die eigene Bibliothek für Clans bereitstellen (die App ruft das einmal auf).</summary>
    public void Attach(SoundLibrary library)
    {
        library.Changed += () => Share.FilesChanged(library.Sounds.Select(s => s.Path));
        Share.FilesChanged(library.Sounds.Select(s => s.Path));
    }

    private void SaveToken() { try { if (string.IsNullOrEmpty(Token)) File.Delete(_tokenFile); else File.WriteAllText(_tokenFile, Token); } catch { } }
    private void Notify() => Changed?.Invoke();

    /// <summary>JSON-Antwort oder Fehlermeldung.</summary>
    public async Task<(JsonNode? obj, string? error)> ApiAsync(string path, HttpMethod? method = null, object? body = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(Server, path));
        if (!string.IsNullOrEmpty(Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            var obj = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
            if (resp.IsSuccessStatusCode) return (obj, null);
            return (null, (string?)obj?["error"] ?? $"Fehler {(int)resp.StatusCode}");
        }
        catch { return (null, "Keine Verbindung zum Server."); }
    }

    private static AccountUser? UserFrom(JsonNode? n) =>
        n is null ? null : new AccountUser((string?)n["id"] ?? "", (string?)n["email"] ?? "", (string?)n["name"] ?? "");

    private static List<ClanSummary> ClansFrom(JsonNode? n)
    {
        var list = new List<ClanSummary>();
        if (n is JsonArray a)
            foreach (var c in a)
                if (c is not null)
                    list.Add(new ClanSummary((string?)c["id"] ?? "", (string?)c["name"] ?? "", (string?)c["role"] ?? "member", (int?)c["members"] ?? 0));
        return list;
    }

    public async Task<bool> RegisterAsync(string email, string name, string password)
    {
        Busy = true; Problem = null; Notify();
        var (_, err) = await ApiAsync("api/account/register", HttpMethod.Post, new { email, name, password });
        Busy = false; Problem = err; Notify();
        return err is null;
    }

    public async Task ResendAsync(string email) => await ApiAsync("api/account/resend", HttpMethod.Post, new { email });

    public async Task<bool> VerifyAsync(string email, string code)
    {
        Busy = true; Problem = null; Notify();
        var (obj, err) = await ApiAsync("api/account/verify", HttpMethod.Post, new { email, code });
        if (err is null && obj is not null) { Token = (string?)obj["token"]; User = UserFrom(obj["user"]); SaveToken(); await RefreshMeAsync(); }
        else Problem = err;
        Busy = false; Notify();
        return err is null;
    }

    public async Task<(bool ok, bool needsVerify)> LoginAsync(string email, string password)
    {
        Busy = true; Problem = null; Notify();
        var (obj, err) = await ApiAsync("api/account/login", HttpMethod.Post, new { email, password });
        if (err is null && obj is not null)
        {
            Token = (string?)obj["token"]; User = UserFrom(obj["user"]); SaveToken();
            await RefreshMeAsync(); Busy = false; Notify();
            return (true, false);
        }
        Problem = err; Busy = false; Notify();
        return (false, err is not null && err.ToLowerInvariant().Contains("bestätig"));
    }

    public void Logout()
    {
        // Den Schlüssel auch auf dem Server ungültig machen
        if (!string.IsNullOrEmpty(Token))
        {
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "api/account/logout"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            _ = _http.SendAsync(req).ContinueWith(_ => { });
        }
        Token = null; User = null; Clans = new(); SaveToken(); Notify();
    }

    /// <summary>Konto mit allen Daten löschen (DSGVO Art. 17). Braucht das Passwort.</summary>
    public async Task<bool> DeleteAccountAsync(string password)
    {
        Busy = true; Problem = null; Notify();
        var (_, err) = await ApiAsync("api/account/delete", HttpMethod.Post, new { password });
        Busy = false;
        if (err is null) { Token = null; User = null; Clans = new(); SaveToken(); }
        else Problem = err;
        Notify();
        return err is null;
    }

    public async Task RefreshMeAsync()
    {
        if (string.IsNullOrEmpty(Token)) return;
        var (obj, err) = await ApiAsync("api/account/me");
        if (obj is not null) { if (UserFrom(obj["user"]) is { } u) User = u; Clans = ClansFrom(obj["clans"]); }
        else if (err is not null && (err.Contains("Nicht eingeloggt") || err.Contains("401"))) Logout();
        Notify();
    }

    public async Task<string?> CreateClanAsync(string name)
    {
        Busy = true; Problem = null; Notify();
        var (obj, err) = await ApiAsync("api/clans", HttpMethod.Post, new { name });
        Busy = false; Problem = err; Notify();
        if (err is null) { await RefreshMeAsync(); return (string?)obj?["clan"]?["inviteCode"]; }
        return null;
    }

    public async Task<string?> JoinClanAsync(string code)
    {
        Busy = true; Problem = null; Notify();
        var (obj, err) = await ApiAsync("api/clans/join", HttpMethod.Post, new { code });
        Busy = false; Problem = err; Notify();
        if (err is null) { await RefreshMeAsync(); return (string?)obj?["status"]; }
        return null;
    }
}

/// <summary>Eine geöffnete Clan-Seite (wie ein Discord-Server): Session, Einladung, Clan-Sounds, Mitglieder.</summary>
public sealed class ClanRoom
{
    private readonly Account _account;
    private readonly Lobby _lobby;
    private readonly SoundLibrary _library;

    public string ClanId { get; }
    public string Name { get; private set; }
    public string Role { get; private set; } = "";
    public bool Owner { get; private set; }
    public string? InviteCode { get; private set; }
    public string? SessionCode { get; private set; }
    public List<ClanMember> Members { get; private set; } = new();
    public List<ClanSound> Sounds { get; private set; } = new();
    public string? Note { get; set; }
    /// <summary>Nicht mehr im Clan (entfernt, ausgetreten, aufgelöst)</summary>
    public bool Gone { get; private set; }
    public bool IsAdmin => Role == "admin";
    public string? MyId => _account.User?.Id;
    public int OnlineCount => Members.Count(m => m.Online);
    /// <summary>Zum Weitergeben, öffnet eine Einladungsseite</summary>
    public string? InviteLink => InviteCode is null ? null : new Uri(Account.Server, $"c/{InviteCode}").ToString();
    public Account Account => _account;
    public string LibraryFolder => _library.Folder;
    public event Action? Changed;

    public ClanRoom(Account account, Lobby lobby, SoundLibrary library, string clanId, string name)
    {
        _account = account; _lobby = lobby; _library = library; ClanId = clanId; Name = name;
    }

    private void Notify() => Changed?.Invoke();

    public async Task LoadAsync()
    {
        var (obj, err) = await _account.ApiAsync($"api/clans/{ClanId}");
        if (err is not null && (err.Contains("nicht in diesem Clan") || err.Contains("gibt es nicht"))) { Gone = true; Notify(); return; }
        if (obj is not null)
        {
            var c = obj["clan"];
            if (c is not null)
            {
                Name = (string?)c["name"] ?? Name;
                Role = (string?)c["role"] ?? "";
                Owner = (bool?)c["owner"] ?? false;
                InviteCode = (string?)c["inviteCode"];
                SessionCode = (string?)c["session"];
            }
            Members = new();
            if (obj["members"] is JsonArray ms)
                foreach (var m in ms) if (m is not null)
                    Members.Add(new ClanMember((string?)m["id"] ?? "", (string?)m["name"] ?? "", (string?)m["role"] ?? "member",
                        (bool?)m["owner"] ?? false, (bool?)m["online"] ?? false, (bool?)m["inSession"] ?? false));
            Members = Members.OrderByDescending(m => m.Online).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        var (so, _) = await _account.ApiAsync($"api/clans/{ClanId}/sounds");
        Sounds = new();
        if (so?["sounds"] is JsonArray sa)
            foreach (var s in sa) if (s is not null) Sounds.Add(new ClanSound((string?)s["id"] ?? "", (string?)s["name"] ?? "", (long?)s["size"] ?? 0, (bool?)s["available"] ?? true, (string?)s["by"]));
        Notify();
    }

    public async Task RotateInviteAsync()
    {
        var (obj, err) = await _account.ApiAsync($"api/clans/{ClanId}/invite", HttpMethod.Post);
        if (err is null) InviteCode = (string?)obj?["inviteCode"];
        Note = err ?? $"Neuer Code: {InviteCode}. Der alte gilt nicht mehr.";
        Notify();
    }

    /// <summary>Jemanden aus dem Clan entfernen (nur Admins, den Besitzer nicht).</summary>
    public async Task KickAsync(ClanMember m)
    {
        var (_, err) = await _account.ApiAsync($"api/clans/{ClanId}/kick", HttpMethod.Post, new { userId = m.Id });
        Note = err ?? $"{m.Name} ist nicht mehr im Clan.";
        await LoadAsync();
    }

    /// <summary>Clan verlassen (alle außer dem Besitzer) bzw. auflösen (nur der Besitzer).</summary>
    public async Task LeaveOrDissolveAsync()
    {
        var (_, err) = await _account.ApiAsync($"api/clans/{ClanId}/{(Owner ? "delete" : "leave")}", HttpMethod.Post);
        if (err is not null) { Note = err; Notify(); return; }
        if (_lobby.Active && _lobby.Code == SessionCode) _lobby.Leave();
        Gone = true;
        await _account.RefreshMeAsync();
        Notify();
    }

    /// <summary>
    /// Eigene Sounds im Clan teilen. Hochgeladen wird nichts: Die Dateien bleiben auf diesem PC und gehen
    /// direkt an die anderen, wenn jemand sie braucht. Dateien von außerhalb kommen dafür in die eigene Bibliothek.
    /// </summary>
    public async Task ShareAsync(IEnumerable<string> paths)
    {
        var picked = paths.ToList();
        var libraryFiles = _library.Sounds.Select(s => s.Path).ToList();
        var (meta, outside) = await Task.Run(() =>
        {
            var inLibrary = libraryFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var meta = new List<object>();
            var outside = new List<(string Path, string Sha)>();
            foreach (var p in picked)
            {
                if (FileHash.Sha256(p) is not { } sha) continue;
                meta.Add(new { id = sha, name = Path.GetFileName(p), size = new FileInfo(p).Length });
                if (!inLibrary.Contains(Path.GetFullPath(p))) outside.Add((p, sha));
            }
            // Was es (mit gleichem Inhalt) schon in der Bibliothek gibt, nicht doppelt anlegen
            var known = outside.Count == 0 ? new HashSet<string>() : libraryFiles.Select(FileHash.Sha256).OfType<string>().ToHashSet();
            return (meta, outside.Where(o => !known.Contains(o.Sha)).Select(o => o.Path).ToList());
        });
        if (outside.Count > 0) _library.Import(outside);
        if (meta.Count == 0) { Note = "Keine Sounds gefunden."; Notify(); return; }
        var (obj, err) = await _account.ApiAsync($"api/clans/{ClanId}/sounds", HttpMethod.Post, new { sounds = meta });
        var skipped = obj?["skipped"]?.AsArray().Count ?? 0;
        Note = err ?? (skipped > 0 ? $"{skipped} Sound(s) zu groß für den Clan (max. 15 MB pro Sound, 150 MB gesamt)."
            : "Geteilt. Die anderen bekommen die Sounds direkt von deinem PC, solange ClipSound bei dir läuft.");
        _account.Share.Refresh(); // gleich melden, dass sie hier liegen – sonst stehen sie kurz als „nicht verfügbar“ da
        await LoadAsync();
    }

    /// <summary>Clan-Sound löschen (nur Admins).</summary>
    public async Task DeleteSoundAsync(ClanSound s)
    {
        var (_, err) = await _account.ApiAsync($"api/clans/{ClanId}/sounds/{s.Id}", HttpMethod.Delete);
        Note = err ?? $"„{s.Title}“ aus dem Clan gelöscht.";
        await LoadAsync();
    }

    /// <summary>Clan-Sound in die eigenen (Solo-)Sounds übernehmen – kommt direkt vom Gerät eines Mitglieds.</summary>
    public async Task TakeToSoloAsync(ClanSound s)
    {
        var mine = _library.Sounds.Select(x => x.Path).ToList();
        if (await Task.Run(() => mine.Any(p => { try { return new FileInfo(p).Length == s.Size && FileHash.Sha256(p) == s.Id; } catch { return false; } })))
        {
            Note = $"„{s.Title}“ ist schon in deinen Sounds.";
            Notify();
            return;
        }
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Account.Server, $"api/clans/{ClanId}/blobs/{s.Id}"));
        if (!string.IsNullOrEmpty(_account.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account.Token);
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var resp = await h.SendAsync(req);
            var data = await resp.Content.ReadAsByteArrayAsync();
            if (!resp.IsSuccessStatusCode || FileHash.Hex(data) != s.Id)
            {
                string? why = null;
                try { why = (string?)JsonNode.Parse(data)?["error"]; } catch { }
                Note = why ?? "Download fehlgeschlagen.";
                Notify();
                return;
            }
            var dir = Path.Combine(Path.GetTempPath(), "ClipSound-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, s.Name);
            await File.WriteAllBytesAsync(tmp, data);
            var r = _library.Import(new[] { tmp });
            try { Directory.Delete(dir, true); } catch { }
            Note = r.Added > 0 ? $"„{s.Title}“ in deine Sounds übernommen." : "Konnte nicht übernommen werden.";
        }
        catch { Note = "Keine Verbindung zum Server."; }
        Notify();
    }

    /// <summary>Clan-Session starten/beitreten → läuft über den normalen Lobby-Kanal.</summary>
    public async Task StartSessionAsync()
    {
        var (obj, err) = await _account.ApiAsync($"api/clans/{ClanId}/session", HttpMethod.Post);
        if (err is not null || obj is null) { Note = err ?? "Session fehlgeschlagen."; Notify(); return; }
        var code = (string?)obj["code"];
        var ticket = (string?)obj["ticket"];
        if (code is null || ticket is null) { Note = "Session fehlgeschlagen."; Notify(); return; }
        if (_lobby.Active) _lobby.Leave();
        _lobby.JoinSession(code, ticket, _account.User?.Name ?? "Clan");
        SessionCode = code;
        Note = $"Session läuft (Code {code}).";
        Notify();
    }
}
