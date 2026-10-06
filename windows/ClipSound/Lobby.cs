using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipSound;

// Lobby: gemeinsam abspielen über den ClipSound-Server (cloud/server.js) – dasselbe Protokoll wie
// die Mac-App und die Webseite, darum geht Crossplay Windows ↔ Mac ↔ Browser.
// Wer die Lobby öffnet (Host), bringt seine Sounds mit; Gäste sehen sie und können sie abspielen.
// Jeder gedrückte Sound startet bei allen zur gleichen Zeit. Behalten nur, wenn beide zustimmen.

public sealed class LobbySound
{
    public required string Id { get; init; }    // sha256 der Datei
    public required string Name { get; init; }  // Dateiname mit Endung
    public long Size { get; init; }
    /// <summary>Datei auf diesem PC (beim Gast im Cache, sobald geladen)</summary>
    public Sound? Local { get; set; }
    public string Title => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(Name), "[-_]+", " ");
}

public sealed record LobbyMember(string Id, string Name, bool Host);

/// <summary>Offene Frage an mich: Gast will einen Sound behalten (an den Host) oder Host bietet einen an (an den Gast)</summary>
public sealed record LobbyRequest(string Req, bool Asked, string SoundId, string SoundName, string By)
{
    public string SoundTitle => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(SoundName), "[-_]+", " ");
}

public enum LobbyPhase { Off, Connecting, Open, Reconnecting }

public sealed class Lobby
{
    public static readonly Uri DefaultServer = new("https://rooms-production-8e28.up.railway.app/");

    public LobbyPhase Phase { get; private set; } = LobbyPhase.Off;
    public string Code { get; private set; } = "";
    public bool IsHost { get; private set; }
    public string HostName { get; private set; } = "";
    public string Me { get; private set; } = "";
    public List<LobbyMember> Members { get; private set; } = new();
    /// <summary>Gast: Sounds des Hosts. Host: seine eigenen, sobald sie auf dem Server sind.</summary>
    public List<LobbySound> Sounds { get; private set; } = new();
    public (int Done, int Total)? Uploaded { get; private set; }
    public bool Active => Phase != LobbyPhase.Off;
    public IEnumerable<LobbyMember> Guests => Members.Where(m => !m.Host && m.Id != Me);

    /// <summary>Irgendwas hat sich geändert – Oberfläche neu zeichnen (immer im UI-Thread)</summary>
    public event Action? Changed;
    /// <summary>Kurze Meldung für die Oberfläche</summary>
    public event Action<string>? Note;
    /// <summary>Lobby ist zu (Grund für eine Meldung)</summary>
    public event Action<string>? Ended;
    public event Action<Sound, TimeSpan>? Play;
    public event Action? Stop;
    public event Action<LobbyRequest>? Request;
    /// <summary>Gast: beide haben zugestimmt – Datei mit Originalnamen in die eigene Bibliothek übernehmen</summary>
    public event Action<string>? Receive;

    public readonly Uri Server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SynchronizationContext _ui;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "ClipSound Lobby");
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private string? _hostToken;
    private string _name = "";
    private int _retry;
    private double _offset;      // Serverzeit − meine Zeit (ms)
    private double _bestRtt = double.MaxValue;
    private System.Threading.Timer? _pingTimer;
    private CancellationTokenSource? _syncCts;
    private readonly HashSet<string> _downloading = new();

    // Host: Hash pro Datei (Pfad → (Größe, Änderungszeit, sha))
    private readonly Dictionary<string, (long, DateTime, string)> _hashCache = new();
    private Dictionary<string, Sound> _hostFiles = new();   // sha → eigene Datei
    private Dictionary<string, string> _hostIds = new();    // Dateiname → sha
    private IReadOnlyList<Sound> _pendingFiles = Array.Empty<Sound>();

    public Lobby(Uri? server = null)
    {
        var env = Environment.GetEnvironmentVariable("CLIPSOUND_SERVER");
        Server = server ?? (env is not null ? new Uri(env.TrimEnd('/') + "/") : DefaultServer);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    private void OnUi(Action a) => _ui.Post(_ => a(), null);
    private void Notify() => OnUi(() => Changed?.Invoke());
    private void Say(string text) => OnUi(() => Note?.Invoke(text));

    // ---------- Öffnen, beitreten, verlassen ----------

    /// <summary>Lobby mit den eigenen Sounds öffnen</summary>
    public async Task OpenAsync(string name, IReadOnlyList<Sound> files)
    {
        if (Active) return;
        _name = name;
        Phase = LobbyPhase.Connecting; Notify();
        try
        {
            var resp = await _http.PostAsync(new Uri(Server, "api/lobbies"),
                new StringContent(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json"));
            var obj = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            if (!resp.IsSuccessStatusCode) throw new LobbyException((string?)obj["error"] ?? "Lobby konnte nicht geöffnet werden.");
            Code = (string)obj["code"]!;
            _hostToken = (string)obj["hostToken"]!;
            IsHost = true;
            _pendingFiles = files;
            Connect();
            HostLibraryChanged(files);
        }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>Mit Code beitreten (Groß-/Kleinschreibung egal)</summary>
    public void Join(string raw, string name)
    {
        if (Active) return;
        var code = new string(raw.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(5).ToArray());
        if (code.Length != 5) { Ended?.Invoke("Der Code hat 5 Zeichen."); return; }
        _name = name;
        Code = code; IsHost = false; _hostToken = null;
        Phase = LobbyPhase.Connecting; Notify();
        Connect();
    }

    public void Leave()
    {
        bool wasHost = IsHost;
        Phase = LobbyPhase.Off;
        _cts?.Cancel();
        try { _socket?.Abort(); } catch { }
        _socket = null;
        _pingTimer?.Dispose(); _pingTimer = null;
        _syncCts?.Cancel();
        Code = ""; IsHost = false; _hostToken = null; HostName = "";
        Members = new(); Sounds = new(); Uploaded = null;
        _hostFiles = new(); _hostIds = new();
        Stop?.Invoke();
        if (!wasHost) try { Directory.Delete(_cache, true); } catch { } // fremde Sounds nicht liegen lassen
        Changed?.Invoke();
    }

    private void Fail(Exception ex)
    {
        var text = ex is LobbyException le ? le.Message : "Keine Verbindung zum ClipSound-Server.";
        OnUi(() => { Leave(); Ended?.Invoke(text); });
    }

    // ---------- Abspielen ----------

    /// <summary>Host: Sound aus der eigenen Bibliothek. false, wenn er (noch) nicht in der Lobby ist.</summary>
    public bool PlayOwn(Sound sound)
    {
        // erst wenn die Datei auf dem Server ist (der Server listet nur fertige Sounds)
        if (Phase != LobbyPhase.Open || !_hostIds.TryGetValue(sound.FileName, out var sha) || !Sounds.Any(s => s.Id == sha)) return false;
        Send(new { type = "play", id = sha });
        return true;
    }

    /// <summary>Gast: Sound des Hosts</summary>
    public void PlayGuest(LobbySound sound)
    {
        if (sound.Local is null) { Note?.Invoke($"„{sound.Title}“ lädt noch …"); return; }
        if (Phase != LobbyPhase.Open) { Note?.Invoke("Keine Verbindung – verbinde neu …"); return; }
        Send(new { type = "play", id = sound.Id });
    }

    public void StopAll() => Send(new { type = "stop" });

    // ---------- Weitergeben (immer mit Zustimmung) ----------

    public void Ask(LobbySound sound)
    {
        Send(new { type = "ask", id = sound.Id });
        Note?.Invoke($"Gefragt, ob du „{sound.Title}“ behalten darfst …");
    }

    public void Offer(Sound sound, LobbyMember member)
    {
        if (!_hostIds.TryGetValue(sound.FileName, out var sha)) { Note?.Invoke($"„{sound.Title}“ ist noch nicht in der Lobby."); return; }
        Send(new { type = "offer", id = sha, to = member.Id });
        Note?.Invoke($"„{sound.Title}“ an {member.Name} angeboten …");
    }

    public void Answer(LobbyRequest request, bool ok) => Send(new { type = "answer", req = request.Req, ok });

    // ---------- Host: eigene Sounds in die Lobby bringen ----------

    public void HostLibraryChanged(IReadOnlyList<Sound> files)
    {
        if (!IsHost || !Active) return;
        _pendingFiles = files;
        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token); // mehrere Änderungen zusammenfassen
                await SyncHostAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { Say("Hochladen hat nicht geklappt – versuche es gleich nochmal."); }
        });
    }

    private async Task SyncHostAsync(CancellationToken ct)
    {
        var token = _hostToken;
        if (token is null) return;
        var list = new List<(Sound Sound, string Sha, long Size)>();
        foreach (var sound in _pendingFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(sound.Path);
                string sha;
                lock (_hashCache)
                {
                    if (_hashCache.TryGetValue(sound.Path, out var c) && c.Item1 == info.Length && c.Item2 == info.LastWriteTimeUtc) sha = c.Item3;
                    else sha = "";
                }
                if (sha == "")
                {
                    using var f = File.OpenRead(sound.Path);
                    sha = Convert.ToHexStringLower(SHA256.HashData(f));
                    lock (_hashCache) _hashCache[sound.Path] = (info.Length, info.LastWriteTimeUtc, sha);
                }
                list.Add((sound, sha, info.Length));
            }
            catch { /* Datei weg oder gesperrt – überspringen */ }
        }
        var files = list.GroupBy(x => x.Sha).ToDictionary(g => g.Key, g => g.First().Sound);
        var ids = list.ToDictionary(x => x.Sound.FileName, x => x.Sha);
        OnUi(() => { _hostFiles = files; _hostIds = ids; });

        var req = new HttpRequestMessage(HttpMethod.Put, new Uri(Server, $"api/lobbies/{Code}/sounds"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { sounds = list.Select(x => new { id = x.Sha, name = x.Sound.FileName, size = x.Size }) }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return;
        var obj = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        var missing = obj["missing"]!.AsArray().Select(x => (string)x!).ToList();
        var skipped = obj["skipped"]!.AsArray().Count;
        int total = list.Select(x => x.Sha).Distinct().Count() - skipped, done = total - missing.Count;
        OnUi(() => { Uploaded = (done, total); Changed?.Invoke(); });
        if (skipped > 0) Say($"{skipped} Sound(s) zu groß für die Lobby (max. 150 MB gesamt).");
        foreach (var sha in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (!files.TryGetValue(sha, out var sound)) continue;
            var up = new HttpRequestMessage(HttpMethod.Put, new Uri(Server, $"api/lobbies/{Code}/blobs/{sha}"))
            {
                Content = new ByteArrayContent(await File.ReadAllBytesAsync(sound.Path, ct)),
            };
            up.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var r = await _http.SendAsync(up, ct);
            if (r.IsSuccessStatusCode) { done++; var d = done; OnUi(() => { Uploaded = (d, total); Changed?.Invoke(); }); }
        }
    }

    // ---------- Verbindung ----------

    private void Connect()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var socket = _socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var b = new UriBuilder(new Uri(Server, "ws")) { Scheme = Server.Scheme == "https" ? "wss" : "ws" };
        var q = $"lobby={Code}&name={Uri.EscapeDataString(_name)}";
        if (_hostToken is not null) q += "&token=" + Uri.EscapeDataString(_hostToken);
        b.Query = q;
        _ = Task.Run(async () =>
        {
            try
            {
                await socket.ConnectAsync(b.Uri, cts.Token);
                for (int i = 0; i < 5; i++) _ = Task.Delay(200 + i * 300).ContinueWith(_ => Ping());
                OnUi(() =>
                {
                    _pingTimer?.Dispose();
                    _pingTimer = new System.Threading.Timer(_ => Ping(), null, 20000, 20000);
                });
                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await socket.ReceiveAsync(buffer, cts.Token);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, res.Count);
                    } while (!res.EndOfMessage);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    OnUi(() => { if (socket == _socket) Handle(JsonNode.Parse(text)!); });
                }
                var code = (int?)socket.CloseStatus ?? 1006;
                OnUi(() => { if (socket == _socket) Closed(code); });
            }
            catch (OperationCanceledException) { }
            catch { OnUi(() => { if (socket == _socket) Closed(1006); }); }
        });
    }

    private void Closed(int code)
    {
        if (!Active) return;
        string? why = code switch
        {
            4404 => "Diese Lobby gibt es nicht (mehr).",
            4401 => "Lobby-Zugang ungültig.",
            4409 => "Die Lobby ist voll.",
            4410 => _closedReason ?? "Die Lobby wurde geschlossen.",
            4429 => "Zu viele falsche Codes – warte ein paar Minuten.",
            _ => null,
        };
        if (why is not null) { Leave(); Ended?.Invoke(why); return; }
        if (Phase == LobbyPhase.Connecting && _retry >= 2) { Leave(); Ended?.Invoke("Keine Verbindung zum ClipSound-Server."); return; }
        // Netz weg o. Ä.: automatisch neu verbinden
        Phase = LobbyPhase.Reconnecting; Changed?.Invoke();
        var delay = TimeSpan.FromSeconds(Math.Min(10, 0.5 * Math.Pow(2, _retry++)));
        _ = Task.Delay(delay).ContinueWith(_ => OnUi(() => { if (Phase == LobbyPhase.Reconnecting) Connect(); }));
    }

    private void Send(object msg)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync();
            try { await socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { }
            finally { _sendLock.Release(); }
        });
    }
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void Ping() => Send(new { type = "ping", t = NowMs });

    private string? _closedReason;

    private void Handle(JsonNode msg)
    {
        switch ((string?)msg["type"])
        {
            case "hello":
                Phase = LobbyPhase.Open; _retry = 0;
                Me = (string?)msg["you"] ?? "";
                HostName = (string?)msg["host"] ?? "";
                if (_bestRtt == double.MaxValue) _offset = (double)msg["serverTime"]! - NowMs;
                SetMembers(msg["members"]); SetSounds(msg["sounds"]);
                if (IsHost) HostLibraryChanged(_pendingFiles); // nach Neuverbindung Liste auffrischen
                break;
            case "sounds": SetSounds(msg["sounds"]); break;
            case "members": SetMembers(msg["members"]); break;
            case "pong":
            {
                double t = (double)msg["t"]!, st = (double)msg["serverTime"]!, rtt = NowMs - t;
                if (rtt < _bestRtt) { _bestRtt = rtt; _offset = st - (t + rtt / 2); }
                return;
            }
            case "play":
            {
                var id = (string)msg["id"]!;
                Sound? local = IsHost ? _hostFiles.GetValueOrDefault(id) : Sounds.FirstOrDefault(s => s.Id == id)?.Local;
                if (local is null) return;
                var delay = Math.Max(0, (double)msg["at"]! - _offset - NowMs);
                Play?.Invoke(local, TimeSpan.FromMilliseconds(delay));
                return;
            }
            case "stop":
                Stop?.Invoke();
                if ((string?)msg["from"] != Me && (string?)msg["by"] is { } by) Note?.Invoke($"{by} hat gestoppt");
                return;
            case "asked":
            case "offered":
                Request?.Invoke(new LobbyRequest((string)msg["req"]!, (string?)msg["type"] == "asked",
                    (string)msg["id"]!, (string)msg["name"]!, (string)msg["by"]!));
                return;
            case "answered": Answered(msg); return;
            case "closed": _closedReason = (string?)msg["reason"]; return;
        }
        Changed?.Invoke();
    }

    private void Answered(JsonNode msg)
    {
        bool ok = (bool?)msg["ok"] ?? false;
        var title = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension((string?)msg["name"] ?? "Sound"), "[-_]+", " ");
        if (IsHost) { if (ok) Note?.Invoke($"„{title}“ wurde weitergegeben."); else if ((string?)msg["kind"] == "offer") Note?.Invoke($"„{title}“ wurde abgelehnt."); return; }
        if (!ok)
        {
            if ((string?)msg["kind"] == "ask") Note?.Invoke((string?)msg["reason"] ?? $"{HostName} möchte „{title}“ nicht hergeben.");
            return;
        }
        // Beide haben zugestimmt → Kopie mit Originalnamen in die eigene Bibliothek
        var sound = Sounds.FirstOrDefault(s => s.Id == (string?)msg["id"]);
        if (sound?.Local is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "ClipSound-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, sound.Name);
            File.Copy(sound.Local.Path, copy);
            Receive?.Invoke(copy);
            Note?.Invoke($"„{sound.Title}“ ist jetzt in deinen Sounds.");
        }
        catch { Note?.Invoke($"„{sound.Title}“ konnte nicht gespeichert werden."); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private void SetMembers(JsonNode? raw)
    {
        if (raw is not JsonArray arr) return;
        Members = arr.Select(m => new LobbyMember((string)m!["id"]!, (string)m["name"]!, (bool?)m["host"] ?? false)).ToList();
    }

    private void SetSounds(JsonNode? raw)
    {
        if (raw is not JsonArray arr) return;
        Sounds = arr.Select(s => new LobbySound { Id = (string)s!["id"]!, Name = (string)s["name"]!, Size = (long?)s["size"] ?? 0 }).ToList();
        foreach (var s in Sounds) s.Local = IsHost ? _hostFiles.GetValueOrDefault(s.Id) : Cached(s);
        if (!IsHost) Download(Sounds.Where(s => s.Local is null).ToList());
    }

    // ---------- Gast: Dateien vorladen, damit der Start sofort klappt ----------

    private string CachePath(LobbySound s)
    {
        var ext = Path.GetExtension(s.Name).ToLowerInvariant();
        return Path.Combine(_cache, s.Id + (ext == "" ? ".mp3" : ext));
    }

    private Sound? Cached(LobbySound s) => File.Exists(CachePath(s)) ? new Sound(CachePath(s)) : null;

    private void Download(List<LobbySound> todo)
    {
        Directory.CreateDirectory(_cache);
        todo = todo.Where(s => _downloading.Add(s.Id)).ToList();
        var code = Code;
        _ = Task.Run(async () =>
        {
            // drei gleichzeitig reicht und schont den Server
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (s, ct) =>
            {
                try
                {
                    var data = await _http.GetByteArrayAsync(new Uri(Server, $"api/lobbies/{code}/blobs/{s.Id}"), ct);
                    if (Convert.ToHexStringLower(SHA256.HashData(data)) != s.Id) return;
                    var dest = CachePath(s);
                    await File.WriteAllBytesAsync(dest, data, ct);
                    OnUi(() =>
                    {
                        if (Code != code) return;
                        var current = Sounds.FirstOrDefault(x => x.Id == s.Id);
                        if (current is not null) current.Local = new Sound(dest);
                        Changed?.Invoke();
                    });
                }
                catch { }
                finally { OnUi(() => _downloading.Remove(s.Id)); }
            });
        });
    }
}

public sealed class LobbyException(string message) : Exception(message);
