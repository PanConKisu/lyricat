using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace LyricsStatus.Services;

public sealed record CheckResult(bool Ok, string? Alias, string? Error, DiscordUser? Discord = null);

public static class AccountCheck
{
    public static async Task<CheckResult> DiscordAsync(HttpClient http, string token)
    {
        try
        {
            var me = await new DiscordClient(http, token).GetMeAsync();
            return new(true, me.DisplayName, null, me);
        }
        catch (DiscordAuthException) { return new(false, null, "An invalid token was provided."); }
        catch (Exception) { return new(false, null, "Couldn't reach Discord to check it."); }
    }

    public static async Task<CheckResult> SpotifyCookieAsync(SpotifyLyrics spotify, string spDc)
    {
        try
        {
            await spotify.EnsureTokenAsync(spDc, force: true);
            return new(true, await spotify.GetDisplayNameAsync(), null);
        }
        catch (SpotifyAuthException e) when (e.Message.Contains("anonymous"))
        {
            return new(false, null, "An invalid cookie was provided.");
        }
        catch (Exception) { return new(false, null, "Couldn't reach Spotify to check it."); }
    }

    public static async Task<CheckResult> SpotifyAppAsync(HttpClient http, string clientId, string clientSecret)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
            using var res = await http.SendAsync(req);
            if (res.IsSuccessStatusCode) return new(true, null, null);
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return new(false, null, "Invalid Client ID or Client Secret.");
            return new(false, null, $"Spotify answered {(int)res.StatusCode} when checking it.");
        }
        catch (Exception) { return new(false, null, "Couldn't reach Spotify to check it."); }
    }
}
