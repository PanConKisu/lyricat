using System.Text;
using LyricsStatus.Services;
using Microsoft.Web.WebView2.Core;

namespace LyricsStatus;

public static class SelfTest
{
    public static async Task<bool> RunAsync(string outPath)
    {
        var report = new StringBuilder();
        var ok = true;

        async Task Check(string name, Func<Task<string>> test)
        {
            try { report.AppendLine($"{name}: OK {await test()}"); }
            catch (Exception e) { ok = false; report.AppendLine($"{name}: FAIL {e.GetType().Name}: {e.Message}"); }
        }

        await Check("version", () => Task.FromResult(Updater.Current.ToString()));
        await Check("config", () => Task.FromResult(Settings.Load().UpdateInterval.ToString()));
        await Check("credentials", () =>
        {
            const string probe = "selftest_probe";
            Secrets.Set(probe, "ok-123");
            var back = Secrets.Get(probe);
            Secrets.Set(probe, null);
            if (back != "ok-123") throw new Exception("round trip failed");
            return Task.FromResult("round trip");
        });
        await Check("join-lines", () =>
        {
            var lines = new List<LyricLine>
            {
                new(0, "A"), new(4, "B"), new(8, "C"), new(12, ""), new(16, "D"), new(20, "E"), new(24, "F"),
            };
            var on = new Settings { JoinFastLines = true, JoinSeparator = "-", PauseText = "♪" };
            var off = new Settings { JoinFastLines = false, PauseText = "♪" };
            string[] expected = ["A - B", "A - B", "C", "♪", "D - E", "D - E", "F"];
            for (var i = 0; i < lines.Count; i++)
            {
                if (Engine.LineFor(lines, i, on) != expected[i])
                    throw new Exception($"line {i}: got '{Engine.LineFor(lines, i, on)}', expected '{expected[i]}'");
                var alone = lines[i].Text.Length == 0 ? "♪" : lines[i].Text;
                if (Engine.LineFor(lines, i, off) != alone)
                    throw new Exception($"line {i} should stay alone when the option is off");
            }
            var blank = new Settings { StatusFormat = "♫ {line}", PauseText = "" };
            var song = new Playback("x", "Title", "Artist", null, 0, 1000, true, DateTime.UtcNow);
            if (Engine.StatusText(blank, song, Engine.LineFor(lines, 3, blank), true) != "")
                throw new Exception("an empty instrumental text should send an empty status");
            if (Engine.StatusText(blank, song, "A", true) != "♫ A")
                throw new Exception("normal lines should keep the format");
            if (DiscordClient.Clip("  ") != "")
                throw new Exception("blank text should stay blank");
            return Task.FromResult("pairs OK, blank instrumentals OK");
        });
        await Check("stats", () =>
        {
            var sealedData = Stats.Seal(1234);
            if (Stats.Unseal(sealedData) != 1234) throw new Exception("round trip failed");
            var edited = (byte[])sealedData.Clone();
            edited[^5] ^= 0x01;
            if (Stats.Unseal(edited) is not null) throw new Exception("an edited file was accepted");
            if (Stats.Unseal("1234"u8.ToArray()) is not null) throw new Exception("plain text was accepted");
            return Task.FromResult("sealed, edits rejected");
        });
        await Check("beat", () =>
        {
            const int rate = 48000; const double bpm = 128, period = 60 / bpm;
            var results = new List<string>();
            foreach (var hint in new double?[] { null, bpm })
            {
                var tracker = new BeatTracker(rate) { TempoHint = hint };
                var rnd = new Random(7);
                var chunk = new float[480];
                for (var start = 0; start < rate * 12; start += chunk.Length)
                {
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        var t = (start + i) / (double)rate;
                        var since = t % period;
                        var kick = since < 0.12 ? Math.Sin(2 * Math.PI * 60 * since) * Math.Exp(-since * 30) : 0;
                        chunk[i] = (float)(0.6 * kick + 0.05 * (rnd.NextDouble() * 2 - 1));
                    }
                    tracker.Process(chunk, (start + chunk.Length) / (double)rate, rate);
                }
                if (tracker.Grid is not { } g) throw new Exception("no beat found");
                if (Math.Abs(g.Bpm - bpm) > 2) throw new Exception($"tempo {g.Bpm:0.0}, expected {bpm}");
                var off = g.Anchor % period; off = Math.Min(off, period - off);
                if (off > 0.04) throw new Exception($"beats {off * 1000:0} ms off");
                results.Add($"{(hint is null ? "guessed" : "told")} {g.Bpm:0.0} BPM, {off * 1000:0} ms off");
            }
            var clock = new BeatClock();
            var jitter = new Random(3);
            var beats = new List<double>();
            for (var now = 0.0; now < 20; now += 0.015)
            {
                var anchor = Math.Floor(now / period) * period + (jitter.NextDouble() * 2 - 1) * 0.04;
                if (clock.Tick(now, new BeatGrid(period, anchor, 1), 0.2, 0) is { } b) beats.Add(b);
            }
            var gaps = beats.Zip(beats.Skip(1), (a, c) => c - a).Skip(4).ToList();
            var spread = gaps.Max() - gaps.Min();
            if (spread > 0.03) throw new Exception($"clock gaps vary by {spread * 1000:0} ms");
            results.Add($"clock steady within {spread * 1000:0} ms");
            return Task.FromResult(string.Join("; ", results));
        });
        await Check("webview2",() => Task.FromResult(CoreWebView2Environment.GetAvailableBrowserVersionString()));
        await Check("window", async () =>
        {
            var w = new MainWindow { ShowActivated = false, Left = -4000, Top = -4000, SkipDisclaimer = true };
            w.Show();
            await Task.Delay(500);
            w.Close();
            return "opened and closed";
        });

        await File.WriteAllTextAsync(outPath, report.ToString());
        return ok;
    }

    public static async Task ScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var w = new MainWindow { SkipDisclaimer = true, ShowActivated = false, Left = -4000, Top = -4000 };
        w.Show();
        await Task.Delay(400);
        w.ShowDemo();

        foreach (var (page, name) in w.DemoPages())
        {
            page();
            await Task.Delay(450);
            Save(w, Path.Combine(folder, name + ".png"));
        }

        w.DemoPages().First().Show();
        await Task.Delay(450);
        w.DemoAdvance(5);
        await Task.Delay(200);
        Save(w, Path.Combine(folder, "lyrics-moving.png"));
        await Task.Delay(500);
        Save(w, Path.Combine(folder, "lyrics-next.png"));
        w.ShowDisclaimerForDemo();
        await Task.Delay(450);
        Save(w, Path.Combine(folder, "disclaimer.png"));
        w.Close();
    }

    static void Save(System.Windows.Window w, string path)
    {
        w.UpdateLayout();
        var content = (System.Windows.FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        png.Save(fs);
    }
}
