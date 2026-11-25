using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LyricsStatus.Services;

public sealed record Release(string Tag, Version Version, string ExeUrl, string? ChecksumUrl, string PageUrl);

public static class Updater
{
    public const string Repo = "PanConKisu/lyricat";
    const string ExeName = "Lyricat.exe";

    public static Version Current =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new(0, 0, 0);

    public static string RepoUrl => $"https://github.com/{Repo}";

    public static async Task<Release?> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= Current) return null;

            string? exe = null, sum = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name == ExeName) exe = url;
                else if (name == ExeName + ".sha256") sum = url;
            }
            return exe is null ? null : new Release(tag, version, exe, sum, root.GetProperty("html_url").GetString() ?? RepoUrl);
        }
        catch (Exception) { return null; }
    }

    public static async Task ApplyAsync(HttpClient http, Release release, CancellationToken ct = default)
    {
        if (release.ChecksumUrl is null)
            throw new InvalidOperationException("This release has no checksum, so it can't be verified. Download it from GitHub instead.");

        var bytes = await http.GetByteArrayAsync(release.ExeUrl, ct);
        var expected = (await http.GetStringAsync(release.ChecksumUrl, ct)).Split(' ', '\t', '\n')[0].Trim().ToLowerInvariant();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expected != actual)
            throw new InvalidOperationException("The download doesn't match its checksum, so it was NOT installed.");

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the running .exe.");
        var fresh = exe + ".new";
        var old = exe + ".old";
        await File.WriteAllBytesAsync(fresh, bytes, ct);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Move(fresh, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    public static void CleanupOld()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch (Exception) { }
    }
}
