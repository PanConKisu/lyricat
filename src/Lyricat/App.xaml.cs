using System.Windows;
using LyricsStatus.Services;

namespace LyricsStatus;

public partial class App : Application
{
    const string InstanceName = "Kiisum.Lyricat";
    Mutex? _instance;
    EventWaitHandle? _wake;

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

        if (e.Args.Contains("--forget"))
        {
            AutoStart.Forget();
            Shutdown(0);
            return;
        }

        _instance = new Mutex(false, InstanceName);
        var waitFor = e.Args.Contains(Updater.UpdatedFlag) ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
        bool owned;
        try { owned = _instance.WaitOne(waitFor); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch (Exception) { }
            Shutdown(0);
            return;
        }

        Updater.CleanupOld();
        try { Paths.MoveOldData(); } catch (Exception) { /* not critical */ }
        try { Secrets.MigrateLegacy(); } catch (Exception) { /* not critical */ }
        AutoStart.Refresh();

        var window = new MainWindow();
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, -1, false);
        SessionEnding += (_, _) => window.Quit();

        if (e.Args.Contains(AutoStart.Flag)) window.StartInTray();
        else window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (Exception) { }
            _instance.Dispose();
        }
        _wake?.Dispose();
        base.OnExit(e);
    }
}
