using System.Windows;
using LyricsStatus.Services;

namespace LyricsStatus;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            var ok = await SelfTest.RunAsync(e.Args[1]);
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--screenshots")
        {
            await SelfTest.ScreenshotsAsync(e.Args[1]);
            Shutdown(0);
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--beat-listen")
        {
            using var beats = new BeatListener();
            var log = new System.Text.StringBuilder();
            beats.Log += m => { lock (log) log.AppendLine(m); };
            beats.Start();
            var seconds = int.Parse(e.Args[1]);
            for (var i = 1; i <= seconds; i++)
            {
                await Task.Delay(1000);
                var raw = beats.RawGrid;
                lock (log) log.AppendLine(raw is null ? $"{i,3}s  -"
                    : $"{i,3}s  {raw.Bpm,6:0.0} BPM  confidence {raw.Confidence:0.00}{(beats.Grid is null ? "  (below threshold)" : "")}");
            }
            File.WriteAllText(e.Args[2], log.ToString());
            Shutdown(0);
            return;
        }

        Updater.CleanupOld();
        try { Paths.MoveOldData(); } catch (Exception) { /* not critical */ }
        try { Secrets.MigrateLegacy(); } catch (Exception) { /* not critical */ }
        new MainWindow().Show();
    }
}
