using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyricsStatus.Services;

public sealed class DiscordAuthException() : Exception("Invalid Discord token (401). Paste a new one in Accounts.");

public sealed record DiscordUser(string Id, string Username, string DisplayName, string AvatarUrl);

public sealed class DiscordClient(HttpClient http, string token)
{
    const string Api = "https://discord.com/api/v9";

    HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.TryAddWithoutValidation("Authorization", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var res = await http.SendAsync(req, ct))
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new DiscordAuthException();
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                double wait = 0;
                try { wait = JsonDocument.Parse(text).RootElement.GetProperty("retry_after").GetDouble(); } catch (Exception) { }
                throw new RateLimitedException("Discord", wait);
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Discord answered {(int)res.StatusCode}");
            return text;
        }
    }

    public async Task<DiscordUser> GetMeAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await SendAsync(Request(HttpMethod.Get, "/users/@me"), ct));
        var r = doc.RootElement;
        var id = r.GetProperty("id").GetString() ?? "0";
        var username = r.GetProperty("username").GetString() ?? "";
        var display = r.TryGetProperty("global_name", out var g) && g.GetString() is { Length: > 0 } gn ? gn : username;
        var avatar = r.TryGetProperty("avatar", out var a) ? a.GetString() : null;
        var url = avatar is not null
            ? $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=128"
            : $"https://cdn.discordapp.com/embed/avatars/{(ulong.TryParse(id, out var n) ? (n >> 22) % 6 : 0)}.png";
        return new DiscordUser(id, username, display, url);
    }

    public async Task<JsonNode?> GetStatusAsync(CancellationToken ct = default)
    {
        var node = JsonNode.Parse(await SendAsync(Request(HttpMethod.Get, "/users/@me/settings"), ct));
        return node?["custom_status"]?.DeepClone();
    }

    public Task SetStatusAsync(JsonNode? customStatus, CancellationToken ct = default) =>
        SendAsync(Request(HttpMethod.Patch, "/users/@me/settings",
            new JsonObject { ["custom_status"] = customStatus?.DeepClone() }), ct);

    public Task SetTextAsync(string text, string emoji, CancellationToken ct = default)
    {
        var hasEmoji = !string.IsNullOrWhiteSpace(emoji);
        if (string.IsNullOrWhiteSpace(text))
        {
            return SetStatusAsync(hasEmoji ? new JsonObject { ["emoji_name"] = emoji.Trim() } : null, ct);
        }
        var status = new JsonObject { ["text"] = Clip(text) };
        if (hasEmoji) status["emoji_name"] = emoji.Trim();
        return SetStatusAsync(status, ct);
    }

    public static string Clip(string text, int limit = 128)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Length < 2) text = (text + "  ")[..2];
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }
}
