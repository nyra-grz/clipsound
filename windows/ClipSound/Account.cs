using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClipSound;

// Konto + Clans gegen den ClipSound-Server (cloud/clans.js) – gleiche API wie Mac/iOS.
// Solo-Modus läuft weiter über Lobby.cs, ganz ohne Konto.

public sealed record AccountUser(string Id, string Email, string Name);
public sealed record ClanSummary(string Id, string Name, string Role, int Members) { public bool IsAdmin => Role == "admin"; }
public sealed record ClanMember(string Id, string Name, string Role);
public sealed record ClanRequest(string UserId, string Name, string Email);
public sealed record ClanSound(string Id, string Name, long Size)
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

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _tokenFile;

    public string? Token { get; private set; }
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

    public void Logout() { Token = null; User = null; Clans = new(); SaveToken(); Notify(); }

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

/// <summary>Eine geöffnete Clan-Seite: Mitglieder, Anfragen, Clan-Sounds, Session.</summary>
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
    public List<ClanRequest> Requests { get; private set; } = new();
    public List<ClanSound> Sounds { get; private set; } = new();
    public string? Note { get; set; }
    public bool IsAdmin => Role == "admin";
    public event Action? Changed;

    public ClanRoom(Account account, Lobby lobby, SoundLibrary library, string clanId, string name)
    {
        _account = account; _lobby = lobby; _library = library; ClanId = clanId; Name = name;
    }

    private void Notify() => Changed?.Invoke();

    public async Task LoadAsync()
    {
        var (obj, _) = await _account.ApiAsync($"api/clans/{ClanId}");
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
                foreach (var m in ms) if (m is not null) Members.Add(new ClanMember((string?)m["id"] ?? "", (string?)m["name"] ?? "", (string?)m["role"] ?? "member"));
            Requests = new();
            if (obj["requests"] is JsonArray rs)
                foreach (var r in rs) if (r is not null) Requests.Add(new ClanRequest((string?)r["userId"] ?? "", (string?)r["name"] ?? "", (string?)r["email"] ?? ""));
        }
        var (so, _) = await _account.ApiAsync($"api/clans/{ClanId}/sounds");
        Sounds = new();
        if (so?["sounds"] is JsonArray sa)
            foreach (var s in sa) if (s is not null) Sounds.Add(new ClanSound((string?)s["id"] ?? "", (string?)s["name"] ?? "", (long?)s["size"] ?? 0));
        Notify();
    }

    public async Task ApproveAsync(string userId, bool ok)
    {
        await _account.ApiAsync($"api/clans/{ClanId}/approve", HttpMethod.Post, new { userId, ok });
        await LoadAsync();
    }

    public async Task RotateInviteAsync()
    {
        var (obj, _) = await _account.ApiAsync($"api/clans/{ClanId}/invite", HttpMethod.Post);
        InviteCode = (string?)obj?["inviteCode"];
        Note = "Neuer Invite-Code: " + (InviteCode ?? "-");
        Notify();
    }

    /// <summary>Lokale Sounds in die Clan-Bibliothek laden.</summary>
    public async Task UploadAsync(IEnumerable<Sound> files)
    {
        var meta = new List<object>();
        var byId = new Dictionary<string, byte[]>();
        foreach (var s in files)
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(s.Path); } catch { continue; }
            var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            meta.Add(new { id = sha, name = Path.GetFileName(s.Path), size = data.Length });
            byId[sha] = data;
        }
        if (meta.Count == 0) return;
        var (obj, err) = await _account.ApiAsync($"api/clans/{ClanId}/sounds", HttpMethod.Post, new { sounds = meta });
        if (err is not null) { Note = err; Notify(); return; }
        if (obj?["missing"] is JsonArray miss)
            foreach (var idNode in miss)
            {
                var id = (string?)idNode;
                if (id is not null && byId.TryGetValue(id, out var data)) await PutBlobAsync(id, data);
            }
        await LoadAsync();
        Note = "In den Clan geladen.";
        Notify();
    }

    private async Task PutBlobAsync(string id, byte[] data)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, new Uri(Account.Server, $"api/clans/{ClanId}/blobs/{id}"));
        if (!string.IsNullOrEmpty(_account.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account.Token);
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) }; await h.SendAsync(req); } catch { }
    }

    /// <summary>Clan-Sound in die eigenen (Solo-)Sounds übernehmen.</summary>
    public async Task TakeToSoloAsync(ClanSound s)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Account.Server, $"api/clans/{ClanId}/blobs/{s.Id}"));
        if (!string.IsNullOrEmpty(_account.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account.Token);
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var resp = await h.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Note = "Download fehlgeschlagen."; Notify(); return; }
            var data = await resp.Content.ReadAsByteArrayAsync();
            var tmp = Path.Combine(Path.GetTempPath(), s.Name);
            await File.WriteAllBytesAsync(tmp, data);
            var r = _library.Import(new[] { tmp });
            try { File.Delete(tmp); } catch { }
            Note = r.Added > 0 ? $"„{s.Title}“ in deine Sounds übernommen." : "Konnte nicht übernommen werden.";
        }
        catch { Note = "Download fehlgeschlagen."; }
        Notify();
    }

    /// <summary>Clan-Session starten/beitreten → läuft über den normalen Lobby-Kanal.</summary>
    public async Task StartSessionAsync()
    {
        var (obj, err) = await _account.ApiAsync($"api/clans/{ClanId}/session", HttpMethod.Post);
        if (err is not null || obj is null) { Note = err ?? "Session fehlgeschlagen."; Notify(); return; }
        var code = (string?)obj["code"];
        if (code is null) return;
        if (_lobby.Active) _lobby.Leave();
        _lobby.JoinSession(code, (string?)obj["hostToken"], _account.User?.Name ?? "Clan");
        SessionCode = code;
        Note = $"Session läuft (Code {code}).";
        Notify();
    }
}
