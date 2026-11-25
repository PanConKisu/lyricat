using System.Text.Json.Nodes;

namespace LyricsStatus.Services;

public abstract record EngineEvent;
public sealed record ConnectedEvent(DiscordUser User) : EngineEvent;
public sealed record ReadyEvent : EngineEvent;
public sealed record TrackEvent(Playback Playback, IReadOnlyList<LyricLine> Lines) : EngineEvent;
public sealed record PlaybackEvent(Playback Playback) : EngineEvent;
public sealed record LineEvent(int Index, int GroupStart, int GroupEnd, string StatusText) : EngineEvent;
public sealed record StatusEvent(string Text) : EngineEvent;
public sealed record SongStatsEvent(int Changes, double PlayedSeconds) : EngineEvent;
public sealed record PausedEvent : EngineEvent;
public sealed record FeaturesEvent(TrackFeatures Features) : EngineEvent;
public sealed record LogEvent(string Message) : EngineEvent;
public sealed record StoppedEvent(string? Error) : EngineEvent;

public sealed class Engine(HttpClient http, SpotifyLyrics lyrics)
{
    static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2.5);

    public event Action<EngineEvent>? Changed;
    void Emit(EngineEvent e) => Changed?.Invoke(e);
    void Log(string m) => Emit(new LogEvent(m));

    public async Task RunAsync(Func<Settings> settings, CancellationToken ct)
    {
        string? error = null;
        DiscordClient? discord = null;
        JsonNode? original = null;
        var sentAnything = false;
        try
        {
            var spDc = Secrets.Get(Secrets.SpDc);
            var spotify = new SpotifyApi(http, Secrets.Get(Secrets.ClientId), Secrets.Get(Secrets.ClientSecret));
            discord = new DiscordClient(http, Secrets.Get(Secrets.DiscordToken));

            Log("Connecting to Discord...");
            var me = await discord.GetMeAsync(ct);
            original = await discord.GetStatusAsync(ct);
            Emit(new ConnectedEvent(me));

            Log("Connecting to Spotify...");
            await spotify.EnsureLoginAsync(Log, ct);
            await lyrics.EnsureTokenAsync(spDc);
            Log("All connected. Play something on Spotify!");
            Emit(new ReadyEvent());

            Playback? playback = null;
            var nextPoll = DateTime.MinValue;
            string? trackId = null, lastSent = null;
            (int, int, int)? lastLineKey = null;
            IReadOnlyList<LyricLine> lines = [];
            var nextAllowed = DateTime.MinValue;
            var songChanges = 0;
            var songSeconds = 0.0;
            var lastTick = DateTime.UtcNow;
            var nextStats = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (playback is { IsPlaying: true }) songSeconds += (now - lastTick).TotalSeconds;
                lastTick = now;
                if (now >= nextStats)
                {
                    nextStats = now.AddSeconds(1);
                    Emit(new SongStatsEvent(songChanges, songSeconds));
                }
                if (now >= nextPoll)
                {
                    try
                    {
                        playback = await spotify.CurrentAsync(ct);
                        nextPoll = now + PollEvery;
                        if (playback is { IsPlaying: true } p && p.DurationMs - p.PositionAt(now) * 1000 < 3000)
                            nextPoll = now.AddSeconds(1);
                        if (playback is not null) Emit(new PlaybackEvent(playback));
                    }
                    catch (RateLimitedException e)
                    {
                        Log(e.Message);
                        nextPoll = now.AddSeconds(Math.Max(e.RetryAfter, 5));
                    }
                    catch (HttpRequestException e)
                    {
                        Log($"Network: {e.Message}");
                        nextPoll = now.AddSeconds(5);
                    }
                }

                var s = settings();
                if (playback is not { IsPlaying: true })
                {
                    if (lastSent is not null)
                    {
                        await PausedAsync(discord, s, original, ct);
                        lastSent = null;
                        lastLineKey = null;
                        Emit(new PausedEvent());
                    }
                    await Task.Delay(500, ct);
                    continue;
                }

                if (playback.Id != trackId)
                {
                    trackId = playback.Id;
                    lastLineKey = null;
                    songChanges = 0;
                    songSeconds = 0;
                    lines = await lyrics.GetAsync(playback.Id, spDc, Log, ct);
                    Log(lines.Count > 0
                        ? $"♫ {playback.Title} — {playback.Artist} ({lines.Count} lines)"
                        : $"♫ {playback.Title} — {playback.Artist} (no synced lyrics)");
                    Emit(new TrackEvent(playback, lines));

                    var features = TrackFeatures.Unknown;
                    try { features = await lyrics.GetFeaturesAsync(playback.Id, spDc, ct); }
                    catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { }
                    if (features.Tempo is { } bpm)
                        Log($"Tempo: {bpm:0} BPM" + (features.Energy is { } en ? $", energy {en:0.00}" : ""));
                    Emit(new FeaturesEvent(features));
                }

                var index = IndexAt(lines, playback.PositionAt(DateTime.UtcNow) + s.LyricsOffset);
                var current = index >= 0 ? LineFor(lines, index, s) : null;
                var text = StatusText(s, playback, current, lines.Count > 0);
                var (groupStart, groupEnd) = GroupFor(lines, index, s);
                var lineKey = (index, groupStart, groupEnd);
                if (lineKey != lastLineKey)
                {
                    lastLineKey = lineKey;
                    Emit(new LineEvent(index, groupStart, groupEnd, text));
                }

                if (text != lastSent && DateTime.UtcNow >= nextAllowed)
                {
                    try
                    {
                        await discord.SetTextAsync(text, s.Emoji, ct);
                        sentAnything = true;
                        lastSent = text;
                        songChanges++;
                        Stats.Add();
                        nextAllowed = DateTime.UtcNow.AddSeconds(s.UpdateInterval);
                        Emit(new StatusEvent(DiscordClient.Clip(text)));
                    }
                    catch (RateLimitedException e) when (!s.StopOnRateLimit)
                    {
                        Log($"{e.Message}; waiting.");
                        nextAllowed = DateTime.UtcNow.AddSeconds(Math.Max(e.RetryAfter, s.UpdateInterval));
                    }
                    catch (HttpRequestException e)
                    {
                        Log($"Network: {e.Message}");
                        nextAllowed = DateTime.UtcNow.AddSeconds(3);
                    }
                }

                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (RateLimitedException e)
        {
            error = $"Stopped: {e.Message}. Discord is rate-limiting you — wait a bit, and consider a slower update speed.";
        }
        catch (Exception e) when (e is SpotifyAuthException or DiscordAuthException)
        {
            error = e.Message;
        }
        catch (Exception e)
        {
            error = $"Unexpected error: {e.Message}";
        }
        finally
        {
            if (discord is not null && sentAnything)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    await discord.SetStatusAsync(original, cts.Token);
                    Log("Your original status is back.");
                }
                catch (Exception) { Log("Couldn't restore your original status — change it in Discord."); }
            }
            Emit(new StoppedEvent(error));
        }
    }

    static async Task PausedAsync(DiscordClient discord, Settings s, JsonNode? original, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(s.PausedText)) await discord.SetStatusAsync(original, ct);
            else await discord.SetTextAsync(s.PausedText, s.Emoji, ct);
        }
        catch (HttpRequestException) { }
    }

    static int IndexAt(IReadOnlyList<LyricLine> lines, double position)
    {
        var i = -1;
        for (var k = 0; k < lines.Count && lines[k].Time <= position; k++) i = k;
        return i;
    }

    public static bool IsInstrumental(string text) => text.Length == 0 || text == "♪";

    public static string Separator(Settings s) =>
        string.IsNullOrWhiteSpace(s.JoinSeparator) ? " " : $" {s.JoinSeparator.Trim()} ";

    public static string LineFor(IReadOnlyList<LyricLine> lines, int index, Settings s)
    {
        string Show(int i) => IsInstrumental(lines[i].Text) ? s.PauseText : lines[i].Text;
        var (start, end) = GroupFor(lines, index, s);
        return start == end ? Show(start) : Show(start) + Separator(s) + Show(end);
    }

    public static (int Start, int End) GroupFor(IReadOnlyList<LyricLine> lines, int index, Settings s)
    {
        if (!s.JoinFastLines || index < 0) return (index, index);
        var separator = Separator(s).Length;
        var i = 0;
        while (i < lines.Count)
        {
            var join = i + 1 < lines.Count
                && !IsInstrumental(lines[i].Text) && !IsInstrumental(lines[i + 1].Text)
                && lines[i].Text.Length + separator + lines[i + 1].Text.Length <= 128;
            var end = join ? i + 1 : i;
            if (index <= end) return (i, end);
            i = end + 1;
        }
        return (index, index);
    }

    public static string StatusText(Settings s, Playback p, string? line, bool hasLyrics)
    {
        string Fill(string template) => template
            .Replace("{title}", p.Title).Replace("{artist}", p.Artist);
        if (!hasLyrics || line is null) return Fill(s.NoLyricsText);
        if (string.IsNullOrWhiteSpace(line)) return "";
        return Fill(s.StatusFormat).Replace("{line}", line);
    }
}
