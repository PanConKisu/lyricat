using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace LyricsStatus.Services;

public sealed record Playback(
    string Id, string Title, string Artist, string? CoverUrl,
    int ProgressMs, int DurationMs, bool IsPlaying, DateTime FetchedAt)
{
    public double PositionAt(DateTime now) =>
        (ProgressMs + (IsPlaying ? (now - FetchedAt).TotalMilliseconds : 0)) / 1000.0;
}

public sealed class SpotifyAuthException(string message) : Exception(message);
public sealed class RateLimitedException(string service, double retryAfter)
    : Exception($"{service} answered 429 (retry after {retryAfter:0.#} s)")
{
    public double RetryAfter { get; } = retryAfter;
}

public sealed class SpotifyApi(HttpClient http, string clientId, string clientSecret)
{
    public const string RedirectUri = "http://127.0.0.1:8888/callback";
    const string Scopes = "user-read-currently-playing user-read-playback-state";

    string? _accessToken;
    DateTime _expires;

    public async Task EnsureLoginAsync(Action<string> log, CancellationToken ct)
    {
        if (Secrets.Get(Secrets.RefreshToken).Length == 0)
            await LoginAsync(log, ct);
        await TokenAsync(ct);
    }

    async Task LoginAsync(Action<string> log, CancellationToken ct)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var url = "https://accounts.spotify.com/authorize?" +
                  $"client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}&state={state}";

        var listener = new TcpListener(IPAddress.Loopback, 8888);
        try { listener.Start(); }
        catch (SocketException)
        {
            throw new SpotifyAuthException("Port 8888 is busy, so Spotify can't send you back. Close whatever uses it and try again.");
        }

        log("Opening your browser to connect Spotify (first time only)...");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream, timeout.Token);
                var query = HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + request).Query);
                if (query["state"] != state)
                {
                    await RespondAsync(stream, "Wrong request, you can close this tab.");
                    continue;
                }
                if (query["code"] is not { Length: > 0 } code)
                {
                    await RespondAsync(stream, "Access was not granted. You can close this tab.");
                    throw new SpotifyAuthException($"You didn't grant access to your Spotify app ({query["error"]}).");
                }
                await RespondAsync(stream, "All set! You can close this tab and go back to Lyricat.");
                await TokenRequestAsync(new()
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                }, ct);
                return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpotifyAuthException("Timed out waiting for Spotify access. Press Start to try again.");
        }
        finally { listener.Stop(); }
    }

    static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, ct);
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var parts = text.Split('\n')[0].Split(' ');
        return parts.Length > 1 ? parts[1] : "/";
    }

    static async Task RespondAsync(NetworkStream stream, string message)
    {
        var html = "<!doctype html><meta charset=utf-8><title>Lyricat</title>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;background:#f6f1fc;color:#4e3384;" +
                   "display:grid;place-items:center;height:100vh;margin:0\">" +
                   $"<h2>{WebUtility.HtmlEncode(message)}</h2></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    async Task TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            if (form["grant_type"] == "refresh_token" && res.StatusCode == HttpStatusCode.BadRequest)
                Secrets.Set(Secrets.RefreshToken, null);
            throw new SpotifyAuthException($"Spotify login failed ({(int)res.StatusCode}). Check your Client ID and Client Secret.");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        _expires = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600);
        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
            Secrets.Set(Secrets.RefreshToken, refresh);
    }

    async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_accessToken is null || DateTime.UtcNow > _expires.AddMinutes(-1))
            await TokenRequestAsync(new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = Secrets.Get(Secrets.RefreshToken),
            }, ct);
        return _accessToken!;
    }

    public async Task<Playback?> CurrentAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
            var sent = DateTime.UtcNow;
            using var res = await http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _accessToken = null; continue; }
            if (res.StatusCode == HttpStatusCode.NoContent) return null;
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitedException("Spotify", res.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Spotify answered {(int)res.StatusCode}");

            var body = await res.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            if (item.TryGetProperty("type", out var type) && type.GetString() != "track") return null;

            var artists = item.TryGetProperty("artists", out var a)
                ? string.Join(", ", a.EnumerateArray().Select(x => x.GetProperty("name").GetString()))
                : "";
            string? cover = null;
            if (item.TryGetProperty("album", out var album) && album.TryGetProperty("images", out var imgs))
                cover = imgs.EnumerateArray()
                    .Select(i => (Url: i.GetProperty("url").GetString(), W: i.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0))
                    .OrderBy(i => Math.Abs(i.W - 300)).FirstOrDefault().Url;

            var midpoint = sent + (DateTime.UtcNow - sent) / 2;
            return new Playback(
                item.GetProperty("id").GetString() ?? "",
                item.GetProperty("name").GetString() ?? "",
                artists,
                cover,
                root.TryGetProperty("progress_ms", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                item.TryGetProperty("duration_ms", out var d) ? d.GetInt32() : 0,
                root.TryGetProperty("is_playing", out var playing) && playing.GetBoolean(),
                midpoint);
        }
        return null;
    }
}
