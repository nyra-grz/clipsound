using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipSound;

// Sounds gehen von Gerät zu Gerät: Der ClipSound-Server speichert keine Audio-Dateien.
// Braucht jemand einen Sound, bittet der Server ein Gerät, das ihn hat („send“), und reicht die
// Datei direkt weiter – gleiches Protokoll wie Mac, iPhone und Webseite (siehe cloud/server.js).

/// <summary>sha256 einer Datei – gemerkt nach Pfad, Größe und Änderungszeit, weil Hashen Zeit kostet.</summary>
public static class FileHash
{
    private static readonly Dictionary<string, (long Size, DateTime Time, string Sha)> Cache = new();

    public static string? Sha256(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            lock (Cache)
                if (Cache.TryGetValue(path, out var c) && c.Size == info.Length && c.Time == info.LastWriteTimeUtc) return c.Sha;
            using var f = File.OpenRead(path);
            var sha = Convert.ToHexStringLower(SHA256.HashData(f));
            lock (Cache) Cache[path] = (info.Length, info.LastWriteTimeUtc, sha);
            return sha;
        }
        catch { return null; } // Datei weg oder gesperrt
    }

    public static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

public static class Relay
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Eine Datei auf Anfrage des Servers an PUT /api/relay/&lt;rid&gt; schicken.</summary>
    public static async Task SendAsync(Uri server, string rid, string path)
    {
        try
        {
            await using var f = File.OpenRead(path);
            using var content = new StreamContent(f);
            content.Headers.ContentType = new("application/octet-stream");
            await Http.PutAsync(new Uri(server, $"api/relay/{rid}"), content);
        }
        catch { /* Server merkt es am Zeitlimit und fragt ein anderes Gerät */ }
    }

    public static void OpenInBrowser(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>
/// Geräte-Verbindung, solange man angemeldet ist: meldet, welche Clan-Sounds in der eigenen Bibliothek
/// liegen, und schickt einen davon, wenn ein anderes Clan-Mitglied ihn braucht.
/// </summary>
public sealed class ClanShare
{
    private readonly Uri _server;
    private readonly object _lock = new();
    private string? _token;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private int _retry;
    private IReadOnlyList<string> _files = Array.Empty<string>();
    private Dictionary<string, long> _wanted = new();   // sha → Größe (Clan-Sounds, nach denen der Server fragt)
    private Dictionary<string, string> _have = new();   // sha → Datei auf diesem PC
    private int _scanRun;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ClanShare(Uri server) { _server = server; }

    public void Start(string token)
    {
        lock (_lock) { if (token == _token && _socket is not null) return; }
        Stop();
        lock (_lock) _token = token;
        Connect();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _token = null;
            _cts?.Cancel();
            try { _socket?.Abort(); } catch { }
            _socket = null;
            _wanted = new(); _have = new();
            _scanRun++;
        }
    }

    /// <summary>Die eigenen Sound-Dateien haben sich geändert.</summary>
    public void FilesChanged(IEnumerable<string> paths)
    {
        lock (_lock) _files = paths.ToList();
        Rescan();
    }

    private void Connect()
    {
        string? token;
        ClientWebSocket socket;
        CancellationTokenSource cts;
        lock (_lock)
        {
            token = _token;
            if (token is null) return;
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource();
            socket = _socket = new ClientWebSocket();
        }
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var b = new UriBuilder(new Uri(_server, "ws")) { Scheme = _server.Scheme == "https" ? "wss" : "ws", Query = "share=1" };
        _ = Task.Run(async () =>
        {
            int code = 1006;
            try
            {
                await socket.ConnectAsync(b.Uri, cts.Token);
                await SendAsync(socket, new { type = "auth", token }); // Token nicht in die Adresse, die landet in Protokollen
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
                    try { Handle(socket, JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!); } catch { }
                }
                code = (int?)socket.CloseStatus ?? 1006;
            }
            catch (OperationCanceledException) { return; }
            catch { }
            Closed(socket, code);
        });
    }

    private void Closed(ClientWebSocket socket, int code)
    {
        lock (_lock)
        {
            if (socket != _socket) return;
            _socket = null;
            if (_token is null) return;
            if (code == 4401) { _token = null; return; } // abgemeldet oder Konto gelöscht
        }
        // Netz weg o. Ä.: später neu verbinden
        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _retry++)));
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_lock) { if (_token is null || _socket is not null) return; }
            Connect();
        });
    }

    private async Task SendAsync(ClientWebSocket socket, object msg)
    {
        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        await _sendLock.WaitAsync();
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { _sendLock.Release(); }
    }

    private void Send(object msg)
    {
        ClientWebSocket? socket;
        lock (_lock) socket = _socket;
        if (socket is not null) _ = SendAsync(socket, msg);
    }

    private void Handle(ClientWebSocket socket, JsonNode msg)
    {
        switch ((string?)msg["type"])
        {
            case "ready":
                _retry = 0;
                break;
            case "want":
            {
                var wanted = new Dictionary<string, long>();
                if (msg["sounds"] is JsonArray arr)
                    foreach (var s in arr)
                        if ((string?)s?["id"] is { } id) wanted[id] = (long?)s["size"] ?? 0;
                lock (_lock) _wanted = wanted;
                Rescan();
                break;
            }
            case "send":
            {
                var rid = (string?)msg["rid"];
                var id = (string?)msg["id"];
                if (rid is null || id is null) return;
                string? path;
                lock (_lock) _have.TryGetValue(id, out path);
                if (path is null) { _ = SendAsync(socket, new { type = "nosend", rid }); return; }
                _ = Task.Run(() => Relay.SendAsync(_server, rid, path));
                break;
            }
        }
    }

    /// <summary>
    /// In den eigenen Dateien nach den gesuchten Clan-Sounds schauen. Gehasht werden nur Dateien,
    /// deren Größe zu einem gesuchten Sound passt – das geht auch bei großen Bibliotheken schnell.
    /// </summary>
    private void Rescan()
    {
        IReadOnlyList<string> files;
        Dictionary<string, long> wanted;
        int run;
        lock (_lock)
        {
            if (_socket is null) return;
            files = _files; wanted = _wanted; run = ++_scanRun;
        }
        _ = Task.Run(() =>
        {
            var sizes = wanted.Values.ToHashSet();
            var found = new Dictionary<string, string>();
            foreach (var path in files)
            {
                long size;
                try { size = new FileInfo(path).Length; } catch { continue; }
                if (!sizes.Contains(size) || FileHash.Sha256(path) is not { } sha || !wanted.ContainsKey(sha)) continue;
                found[sha] = path;
            }
            lock (_lock)
            {
                if (run != _scanRun) return;
                _have = found;
            }
            Send(new { type = "have", ids = found.Keys.ToArray() });
        });
    }
}
