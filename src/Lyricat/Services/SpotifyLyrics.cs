using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace LyricsStatus.Services;

public sealed record LyricLine(double Time, string Text);

public sealed class SpotifyLyrics(HttpClient http, Dispatcher ui, Func<IntPtr> hostWindow)
{
    const string LyricsUrl = "https://spclient.wg.spotify.com/color-lyrics/v2/track/{0}?format=json&vocalRemoval=false&market=from_token";

    string? _token, _clientToken, _userAgent;
    DateTime _expires;
    readonly Dictionary<string, IReadOnlyList<LyricLine>> _cache = [];

    public async Task EnsureTokenAsync(string spDc, bool force = false)
    {
        if (!force && _token is not null && DateTime.UtcNow < _expires.AddMinutes(-2)) return;
        await ui.InvokeAsync(() => CaptureAsync(spDc)).Task.Unwrap();
    }

    async Task CaptureAsync(string spDc)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewData);
        var options = env.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        var controller = await env.CreateCoreWebView2ControllerAsync(hostWindow(), options);
        try
        {
            controller.IsVisible = false;
            var web = controller.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.IsMuted = true;

            var cookie = web.CookieManager.CreateCookie("sp_dc", spDc, ".spotify.com", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = true;
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            web.CookieManager.AddOrUpdateCookie(cookie);

            var tokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clientTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.WebResourceResponseReceived += async (_, e) =>
            {
                var uri = e.Request.Uri;
                var target = uri.Contains("open.spotify.com/api/token") ? tokenTcs
                           : uri.Contains("clienttoken.spotify.com/v1/clienttoken") ? clientTcs
                           : null;
                if (target is null || target.Task.IsCompleted || e.Response.StatusCode != 200) return;
                try
                {
                    using var stream = await e.Response.GetContentAsync();
                    using var reader = new StreamReader(stream);
                    target.TrySetResult(await reader.ReadToEndAsync());
                }
                catch (Exception) { /* body not available: wait for the next one */ }
            };

            web.Navigate("https://open.spotify.com/");
            string tokenJson;
            try { tokenJson = await tokenTcs.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { throw new SpotifyAuthException("Couldn't reach Spotify's web player (timeout)."); }
            string? clientJson = null;
            try { clientJson = await clientTcs.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { }

            using (var doc = JsonDocument.Parse(tokenJson))
            {
                var root = doc.RootElement;
                var anonymous = !root.TryGetProperty("isAnonymous", out var a) || a.GetBoolean();
                if (anonymous || !root.TryGetProperty("accessToken", out var t))
                    throw new SpotifyAuthException("Spotify returned an anonymous token: your sp_dc cookie is missing, wrong or expired.");
                _token = t.GetString();
                _expires = root.TryGetProperty("accessTokenExpirationTimestampMs", out var exp)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()).UtcDateTime
                    : DateTime.UtcNow.AddMinutes(30);
            }
            if (clientJson is not null)
            {
                using var doc = JsonDocument.Parse(clientJson);
                if (doc.RootElement.TryGetProperty("granted_token", out var g) && g.TryGetProperty("token", out var ct))
                    _clientToken = ct.GetString();
            }
            _userAgent = web.Settings.UserAgent;
        }
        finally
        {
            controller.Close();
        }
    }

    public async Task<IReadOnlyList<LyricLine>> GetAsync(string trackId, string spDc, Action<string> log, CancellationToken ct)
    {
        if (_cache.TryGetValue(trackId, out var cached)) return cached;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureTokenAsync(spDc, force: attempt > 0);
            using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(LyricsUrl, Uri.EscapeDataString(trackId)));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.TryAddWithoutValidation("App-Platform", "WebPlayer");
            if (_clientToken is not null) req.Headers.TryAddWithoutValidation("client-token", _clientToken);
            if (_userAgent is not null) req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (res.StatusCode == HttpStatusCode.NotFound) return Remember(trackId, []);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                log("Spotify: too many lyrics requests, skipping this song.");
                return [];
            }
            if (!res.IsSuccessStatusCode)
            {
                log($"Spotify answered {(int)res.StatusCode} when asking for the lyrics.");
                return [];
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("lyrics", out var lyrics) ||
                !lyrics.TryGetProperty("syncType", out var sync) || sync.GetString() != "LINE_SYNCED")
                return Remember(trackId, []);

            var lines = lyrics.GetProperty("lines").EnumerateArray()
                .Select(l => new LyricLine(
                    double.Parse(l.GetProperty("startTimeMs").GetString() ?? "0") / 1000.0,
                    (l.TryGetProperty("words", out var w) ? w.GetString() : "")?.Trim() ?? ""))
                .OrderBy(l => l.Time)
                .ToList();
            return Remember(trackId, lines);
        }
        return [];
    }

    const string FeaturesUrl = "https://spclient.wg.spotify.com/audio-attributes/v1/audio-features/{0}?format=json";
    readonly Dictionary<string, TrackFeatures> _features = [];

    public async Task<TrackFeatures> GetFeaturesAsync(string trackId, string spDc, CancellationToken ct)
    {
        if (_features.TryGetValue(trackId, out var known)) return known;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureTokenAsync(spDc, force: attempt > 0);
            using var res = await http.SendAsync(SpclientRequest(string.Format(FeaturesUrl, Uri.EscapeDataString(trackId))), ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (res.StatusCode == HttpStatusCode.NotFound) return _features[trackId] = TrackFeatures.Unknown;
            if (!res.IsSuccessStatusCode) return TrackFeatures.Unknown;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("audio_features", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                root = list[0];
            double? Number(string name, double min, double max) =>
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.GetDouble() is var d && d >= min && d <= max ? d : null;
            var features = new TrackFeatures(Number("tempo", 30, 300), Number("energy", 0, 1),
                Number("danceability", 0, 1), Number("speechiness", 0, 1));
            if (_features.Count > 500) _features.Clear();
            return _features[trackId] = features;
        }
        return TrackFeatures.Unknown;
    }

    public async Task<string?> GetDisplayNameAsync()
    {
        try
        {
            using var req = SpclientRequest("https://api.spotify.com/v1/me");
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("display_name", out var n) && n.GetString() is { Length: > 0 } name ? name : null;
        }
        catch (Exception) { return null; }
    }

    HttpRequestMessage SpclientRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.TryAddWithoutValidation("App-Platform", "WebPlayer");
        if (_clientToken is not null) req.Headers.TryAddWithoutValidation("client-token", _clientToken);
        if (_userAgent is not null) req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        return req;
    }

    IReadOnlyList<LyricLine> Remember(string id, IReadOnlyList<LyricLine> lines)
    {
        if (_cache.Count > 200) _cache.Clear();
        _cache[id] = lines;
        return lines;
    }
}

public sealed record TrackFeatures(double? Tempo, double? Energy, double? Danceability, double? Speechiness)
{
    public static readonly TrackFeatures Unknown = new(null, null, null, null);
}
