using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LyricsStatus.Services;

namespace LyricsStatus;

public partial class MainWindow : Window
{
    const string Disclaimer =
        "Lyricat is an independent, unofficial project. It is not affiliated with, " +
        "endorsed by, or connected to Spotify or Discord in any way. Spotify and Discord are " +
        "trademarks of their respective owners.\n\n" +
        "To work, it uses your Discord user token and internal Spotify endpoints, which goes against " +
        "their terms of service and could get your accounts limited or banned.\n\n" +
        "You use this program entirely at your own risk. The author takes no responsibility for " +
        "any consequences that may come from using it.";

    static readonly (string Key, string Label, bool Secret, string Help)[] SecretFieldInfo =
    [
        (Secrets.DiscordToken, "Discord token", true,
            "1. Open discord.com/app in your browser.\n2. Press F12 → Network tab.\n3. Type 'api' in the filter and switch channels.\n" +
            "4. Click a request → Headers → copy 'authorization'.\n\nNever share it: it gives full access to your account."),
        (Secrets.SpDc, "Spotify sp_dc cookie", true,
            "1. Open open.spotify.com in your browser, logged in.\n2. Press F12 → Application → Cookies → open.spotify.com.\n" +
            "3. Copy the value of 'sp_dc'.\n\nUsed to read the lyrics. Never share it."),
        (Secrets.ClientId, "Spotify app Client ID", false,
            "1. Create an app at developer.spotify.com/dashboard.\n2. Tick only 'Web API'.\n" +
            "3. Redirect URI: http://127.0.0.1:8888/callback\n4. Copy the Client ID from its settings."),
        (Secrets.ClientSecret, "Spotify app Client Secret", true,
            "In your Spotify app's settings, click 'View client secret' and copy it."),
    ];

    static readonly HttpClient Http = CreateHttp();

    readonly Settings _settings = Settings.Load();
    readonly object _settingsLock = new();
    readonly Dictionary<string, Func<string>> _secretReaders = [];
    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    SpotifyLyrics? _lyrics;
    CancellationTokenSource? _run;
    Task? _runTask;
    Playback? _playback;
    readonly LyricsCarousel _carousel;
    Release? _release;
    bool _loading = true;
    bool _closing;
    string? _coverUrl;

    public bool SkipDisclaimer { get; init; }

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{Updater.Current}";
        AboutVersion.Text = $"Lyricat v{Updater.Current}";
        ShowStats();
        DisclaimerText.Text = Disclaimer;
        DisclaimerAbout.Text = Disclaimer;

        _carousel = new LyricsCarousel(LyricsCanvas,
            ((SolidColorBrush)FindResource("Deep")).Color,
            ((SolidColorBrush)FindResource("Muted")).Color,
            (FontFamily)FindResource("DisplayFont"),
            () => CurrentSettings().PauseText is { Length: > 0 } p && !string.IsNullOrWhiteSpace(p) ? p : "♪");
        _carousel.SetSong([], "Waiting for music");

        BuildSecretFields();
        LoadSettingsIntoForm();
        _loading = false;

        _progressTimer.Tick += (_, _) => UpdateProgress();
        _beatTimer.Tick += (_, _) => BeatTick();
        _pulseTimer.Tick += (_, _) => PulseHeart();
        _beats.Log += m => Dispatcher.BeginInvoke(() => AppendLog(m));
        _beats.Lost += () => Task.Delay(2000).ContinueWith(_ => Dispatcher.BeginInvoke(() => _beats.Restart()));
        StateChanged += (_, _) => UpdateEars();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };

        SourceInitialized += (_, _) => ApplyWindowEffects();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lyricat/{Updater.Current}");
        return http;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _lyrics = new SpotifyLyrics(Http, Dispatcher, () => hwnd);

        if (!_settings.AcceptedDisclaimer && !SkipDisclaimer) DisclaimerOverlay.Visibility = Visibility.Visible;
        if (!Secrets.HasAll()) NavAccounts.IsChecked = true;

        if (_settings.CheckUpdates && !SkipDisclaimer)
            ShowRelease(await Updater.CheckAsync(Http));
    }

    void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    void DragArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _beats.Stop();
        if (_runTask is null || _runTask.IsCompleted || _closing) return;
        e.Cancel = true;
        _closing = true;
        SetShareState("Stopping...", on: true, busy: true);
        _run?.Cancel();
        await Task.WhenAny(_runTask, Task.Delay(8000));
        Close();
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageNow is null) return;
        PageNow.Visibility = NavNow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAccounts.Visibility = NavAccounts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var page = new[] { PageNow, PageAccounts, PageSettings, (FrameworkElement)PageAbout }
            .First(p => p.Visibility == Visibility.Visible);
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void AcceptDisclaimer_Click(object sender, RoutedEventArgs e)
    {
        lock (_settingsLock) { _settings.AcceptedDisclaimer = true; _settings.Save(); }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => DisclaimerOverlay.Visibility = Visibility.Collapsed;
        DisclaimerOverlay.BeginAnimation(OpacityProperty, fade);
    }

    async void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_runTask is { IsCompleted: false })
        {
            SetShareState("Stopping...", on: true, busy: true);
            _run?.Cancel();
            return;
        }
        if (!_settings.AcceptedDisclaimer)
        {
            DisclaimerOverlay.Visibility = Visibility.Visible;
            DisclaimerOverlay.Opacity = 1;
            return;
        }
        if (!Secrets.HasAll())
        {
            Toast_Show("Fill in your accounts first");
            NavAccounts.IsChecked = true;
            return;
        }

        SetShareState("Starting...", on: true, busy: true);
        _run = new CancellationTokenSource();
        var engine = new Engine(Http, _lyrics!);
        engine.Changed += ev => Dispatcher.BeginInvoke(() => OnEngine(ev));
        _runTask = Task.Run(() => engine.RunAsync(CurrentSettings, _run.Token));
        await _runTask;
    }

    Settings CurrentSettings()
    {
        lock (_settingsLock) return _settings.Clone();
    }

    void SetRunning(bool running)
    {
        SetShareState(running ? "Sharing" : "Not sharing", on: running, busy: false);
        if (!running) _progressTimer.Stop();
    }

    void SetShareState(string text, bool on, bool busy)
    {
        ShareText.Text = text;
        ShareToggle.IsEnabled = !busy || text == "Starting...";
        ShareDot.Fill = (Brush)FindResource(busy ? "Questionable" : on ? "Safe" : "Faint");
        ShareToggle.Background = (Brush)FindResource(on && !busy ? "PrimarySoft" : "Surface");
        ShareToggle.BorderBrush = (Brush)FindResource(on && !busy ? "Primary" : "Line");
        ShareTrack.Background = on ? (Brush)FindResource("Primary") : new SolidColorBrush(Color.FromRgb(0x4A, 0x3F, 0x72));
        ShareKnobMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(on ? 16 : 0, TimeSpan.FromMilliseconds(160)));
    }

    void OnEngine(EngineEvent ev)
    {
        switch (ev)
        {
            case ConnectedEvent c:
                DiscordName.Text = c.User.DisplayName;
                DiscordUser.Text = "@" + c.User.Username;
                LoadImage(c.User.AvatarUrl, img => Avatar.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill });
                break;

            case TrackEvent t:
                _beats.SetTempo(null);
                _energy = null;
                ShowFeatures(TrackFeatures.Unknown);
                _songChanges = 0;
                _songSeconds = 0;
                ShowStats();
                ShowPlayback(t.Playback);
                _carousel.SetSong(t.Lines, CurrentSettings().NoLyricsText);
                break;

            case PlaybackEvent p:
                ShowPlayback(p.Playback);
                break;

            case LineEvent l:
                _carousel.SetFallback(l.StatusText);
                _carousel.MoveTo(l.Index, l.GroupStart, l.GroupEnd);
                break;

            case StatusEvent s:
                DiscordStatus.Text = (s.Text, _settings.Emoji.Trim()) switch
                {
                    ("", "") => "(empty status)",
                    ("", var e) => e,
                    (var t, "") => t,
                    (var t, var e) => $"{e} {t}",
                };
                ShowStats();
                break;

            case SongStatsEvent st:
                _songChanges = st.Changes;
                _songSeconds = st.PlayedSeconds;
                ShowStats();
                break;

            case PausedEvent:
                _progressTimer.Stop();
                _carousel.SetSong([], "Paused");
                DiscordStatus.Text = string.IsNullOrWhiteSpace(_settings.PausedText) ? "(your usual status)" : _settings.PausedText;
                break;

            case FeaturesEvent f:
                _beats.SetTempo(f.Features.Tempo);
                _energy = f.Features.Energy;
                ShowFeatures(f.Features);
                break;

            case ReadyEvent:
                SetRunning(true);
                break;

            case LogEvent log:
                AppendLog(log.Message);
                break;

            case StoppedEvent stopped:
                SetRunning(false);
                _beats.SetTempo(null);
                _energy = null;
                ShowFeatures(TrackFeatures.Unknown);
                if (stopped.Error is not null)
                {
                    AppendLog(stopped.Error);
                    if (!_closing) MessageBox.Show(this, stopped.Error, "Lyricat", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
        }
    }

    int _songChanges;
    double _songSeconds;

    void ShowStats()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var session = string.Format(inv, "Changes during this session: {0:N0}", Stats.Session);
        if (_songSeconds > 0)
        {
            var average = _songChanges / Math.Max(_songSeconds, 5) * 5;
            session = string.Format(inv, "During this song the status changed {0:0.0} times every 5 seconds  |  ", average) + session;
        }
        StatsText.Text = session;
        AllTimeText.Text = string.Format(inv, "All-time status changes: {0:N0}", Stats.Total)
            + (Stats.WasReset ? "  (the stats file was modified or moved, so the count started over)" : "");
    }

    void ShowPlayback(Playback p)
    {
        _playback = p;
        SongTitle.Text = p.Title;
        SongArtist.Text = p.Artist;
        TimeTotal.Text = Format(p.DurationMs / 1000.0);
        if (p.CoverUrl != _coverUrl)
        {
            _coverUrl = p.CoverUrl;
            Cover.Background = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            if (p.CoverUrl is not null)
                LoadImage(p.CoverUrl, img =>
                {
                    Cover.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                    CoverPlaceholder.Visibility = Visibility.Collapsed;
                });
        }
        if (p.IsPlaying) _progressTimer.Start(); else _progressTimer.Stop();
        UpdateProgress();
    }

    void UpdateProgress()
    {
        if (_playback is not { } p || p.DurationMs <= 0) return;
        var pos = Math.Min(p.PositionAt(DateTime.UtcNow), p.DurationMs / 1000.0);
        ProgressScale.ScaleX = pos * 1000 / p.DurationMs;
        TimeNow.Text = Format(pos);
    }

    static string Format(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    void AppendLog(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        if (LogBox.LineCount > 300) LogBox.Text = string.Join("\n", LogBox.Text.Split('\n').TakeLast(200));
        LogBox.ScrollToEnd();
    }

    static void LoadImage(string url, Action<BitmapImage> done)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.DecodePixelWidth = 220;
                    img.StreamSource = new MemoryStream(bytes);
                    img.EndInit();
                    img.Freeze();
                    done(img);
                });
            }
            catch (Exception) { /* no picture: keep the placeholder */ }
        });
    }

    void BuildSecretFields()
    {
        foreach (var (key, label, secret, help) in SecretFieldInfo)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 18, 18) };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var helpIcon = new TextBlock
            {
                Text = "", Style = (Style)FindResource("Icon"), FontSize = 13,
                Foreground = (Brush)FindResource("Primary"), Cursor = Cursors.Help,
                ToolTip = new ToolTip { Content = new TextBlock { Text = help } }, Margin = new Thickness(6, 0, 0, 0),
            };
            ToolTipService.SetInitialShowDelay(helpIcon, 100);
            ToolTipService.SetShowDuration(helpIcon, 60000);
            DockPanel.SetDock(helpIcon, Dock.Left);
            header.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0) });
            header.Children.Add(helpIcon);
            panel.Children.Add(header);

            var value = Secrets.Get(key);
            if (secret)
            {
                var grid = new Grid();
                var pass = new PasswordBox { Password = value, Padding = new Thickness(12, 9, 40, 9) };
                var plain = new TextBox { Text = value, Visibility = Visibility.Collapsed, Padding = new Thickness(12, 9, 40, 9) };
                var eye = new Button
                {
                    Style = (Style)FindResource("IconButton"), Content = "", Width = 30, Height = 28,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0), ToolTip = "Show / hide",
                };
                eye.Click += (_, _) =>
                {
                    var show = plain.Visibility != Visibility.Visible;
                    if (show) plain.Text = pass.Password; else pass.Password = plain.Text;
                    plain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    pass.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                    eye.Content = show ? "" : "";
                };
                grid.Children.Add(pass);
                grid.Children.Add(plain);
                grid.Children.Add(eye);
                panel.Children.Add(grid);
                _secretReaders[key] = () => plain.Visibility == Visibility.Visible ? plain.Text : pass.Password;
            }
            else
            {
                var box = new TextBox { Text = value };
                panel.Children.Add(box);
                _secretReaders[key] = () => box.Text;
            }
            SecretFields.Children.Add(panel);
        }
    }

    async void SaveAccounts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (key, read) in _secretReaders) Secrets.Set(key, read());
        var missing = SecretFieldInfo.Where(f => Secrets.Get(f.Key).Length == 0).Select(f => f.Label).ToList();
        if (missing.Count > 0)
        {
            AccountsResult.Text = "Saved. Still missing: " + string.Join(", ", missing) + ".";
            return;
        }
        AccountsResult.Text = "Saved. Checking Discord...";
        try
        {
            var me = await new DiscordClient(Http, Secrets.Get(Secrets.DiscordToken)).GetMeAsync();
            AccountsResult.Text = $"Saved ✓  Discord: connected as @{me.Username}. Spotify is checked when you press Start.";
            DiscordName.Text = me.DisplayName;
            DiscordUser.Text = "@" + me.Username;
            LoadImage(me.AvatarUrl, img => Avatar.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill });
        }
        catch (DiscordAuthException ex) { AccountsResult.Text = "Saved, but " + ex.Message; }
        catch (Exception ex) { AccountsResult.Text = $"Saved, but couldn't reach Discord: {ex.Message}"; }
    }

    void OpenDashboard_Click(object sender, RoutedEventArgs e) => Open("https://developer.spotify.com/dashboard");
    void OpenRepo_Click(object sender, RoutedEventArgs e) => Open(Updater.RepoUrl);
    void OpenDracani_Click(object sender, RoutedEventArgs e) => Open("https://linktr.ee/Dracani");

    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    void LoadSettingsIntoForm()
    {
        _loading = true;
        SpeedSlider.Value = _settings.UpdateInterval;
        OffsetSlider.Value = _settings.LyricsOffset;
        FormatBox.Text = _settings.StatusFormat;
        EmojiBox.Text = _settings.Emoji;
        NoLyricsBox.Text = _settings.NoLyricsText;
        PauseBox.Text = _settings.PauseText;
        PausedBox.Text = _settings.PausedText;
        StopOn429.IsChecked = _settings.StopOnRateLimit;
        CheckUpdates.IsChecked = _settings.CheckUpdates;
        JoinLines.IsChecked = _settings.JoinFastLines;
        SeparatorBox.Text = _settings.JoinSeparator;
        (_settings.Mascot == Mascots.Dracani ? MascotDracani : MascotConcept).IsChecked = true;
        EarsDance.IsChecked = _settings.EarsDance;
        ReduceMotion.IsChecked = _settings.ReduceMotion;
        ShowMascot(_settings.Mascot);
        UpdatePulse();
        ShowSeparator();
        ShowSpeed(_settings.UpdateInterval);
        ShowOffset(_settings.LyricsOffset);
        _loading = false;
    }

    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowSpeed(e.NewValue);
        Setting_Changed(sender, e);
    }

    void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowOffset(e.NewValue);
        Setting_Changed(sender, e);
    }

    void ShowSpeed(double value)
    {
        if (SpeedValue is null) return;
        SpeedValue.Text = $"{value:0.0} s";
        var (label, icon, brush, soft, explain) = RiskLevel.For(value) switch
        {
            Risk.Safe => ("Safe", "", "Safe", "SafeSoft",
                "Gentle on Discord. In fast songs some lines will be skipped, but your account stays comfy."),
            Risk.Questionable => ("Questionable", "", "Questionable", "QuestionableSoft",
                "Follows the lyrics more closely, but Discord might start to notice. Use it with care."),
            _ => ("Dangerous!", "", "Dangerous", "DangerousSoft",
                "Very likely to get rate-limited, and it puts your account at real risk. Not recommended."),
        };
        RiskText.Text = label;
        RiskIcon.Text = icon;
        RiskText.Foreground = RiskIcon.Foreground = (Brush)FindResource(brush);
        RiskChip.Background = (Brush)FindResource(soft);
        SpeedSlider.Foreground = (Brush)FindResource(brush == "Safe" ? "Primary" : brush);
        RiskExplain.Text = explain;
    }

    void ShowSeparator()
    {
        if (SeparatorPreview is null) return;
        var on = JoinLines.IsChecked == true;
        SeparatorRow.Opacity = on ? 1 : 0.45;
        SeparatorBox.IsEnabled = on;
        SeparatorPreview.Text = "e.g.  line one" + Engine.Separator(new Settings { JoinSeparator = SeparatorBox.Text }) + "line two";
    }

    void ShowOffset(double value)
    {
        if (OffsetValue is null) return;
        OffsetValue.Text = $"{(value >= 0 ? "+" : "")}{value:0.0} s";
    }

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || FormatBox is null) return;
        lock (_settingsLock)
        {
            _settings.UpdateInterval = SpeedSlider.Value;
            _settings.LyricsOffset = OffsetSlider.Value;
            _settings.StatusFormat = FormatBox.Text;
            _settings.Emoji = EmojiBox.Text;
            _settings.NoLyricsText = NoLyricsBox.Text;
            _settings.PauseText = PauseBox.Text;
            _settings.PausedText = PausedBox.Text;
            _settings.StopOnRateLimit = StopOn429.IsChecked == true;
            _settings.CheckUpdates = CheckUpdates.IsChecked == true;
            _settings.JoinFastLines = JoinLines.IsChecked == true;
            _settings.JoinSeparator = SeparatorBox.Text;
            _settings.Mascot = MascotDracani.IsChecked == true ? Mascots.Dracani : Mascots.Concept;
            _settings.EarsDance = EarsDance.IsChecked == true;
            _settings.ReduceMotion = ReduceMotion.IsChecked == true;
        }
        ShowMascot(_settings.Mascot);
        UpdatePulse();
        ShowSeparator();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void ShowMascot(string mascot)
    {
        var dracani = mascot == Mascots.Dracani;
        Mascot.Visibility = dracani ? Visibility.Collapsed : Visibility.Visible;
        DracaniRig.Visibility = dracani ? Visibility.Visible : Visibility.Collapsed;
        EarsDance.IsEnabled = dracani;
        UpdateEars();
    }

    readonly BeatListener _beats = new();
    readonly DispatcherTimer _beatTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    readonly BeatClock _clock = new();
    double? _energy;
    int _beatCount;
    const double DanceEnergy = 0.7;
    internal bool _demo;

    const double OutputLatency = 0.03;

    void UpdateEars()
    {
        bool want;
        lock (_settingsLock)
            want = !_demo && _settings.Mascot == Mascots.Dracani && _settings.EarsDance && WindowState != WindowState.Minimized;
        if (want == _beatTimer.IsEnabled) return;
        if (want) { _clock.Reset(); _beats.Start(); _beatTimer.Start(); }
        else { _beats.Stop(); _beatTimer.Stop(); }
        UpdatePulse();
    }

    void BeatTick()
    {
        var grid = _beats.Grid;
        var now = BeatListener.Now;
        var rise = grid is null ? 0 : Math.Min(0.21, grid.Period * 0.4);
        if (_clock.Tick(now, grid, rise + 0.008, OutputLatency) is not { } beat) return;
        var lead = Math.Max(0.06, beat - now);
        FlickEars(lead, Math.Min(0.25, grid!.Period * 0.5));
        Task.Delay(TimeSpan.FromSeconds(Math.Max(0, lead - 0.08))).ContinueWith(_ => Dispatcher.BeginInvoke(PulseHeart));
    }

    TrackFeatures _features = TrackFeatures.Unknown;
    readonly DispatcherTimer _pulseTimer = new(DispatcherPriority.Render);

    void ShowFeatures(TrackFeatures f)
    {
        _features = f;
        BpmText.Text = f.Tempo is { } bpm ? $"{bpm:0} BPM" : "— BPM";
        SetBar(EnergyFill, EnergyValue, f.Energy);
        SetBar(DanceFill, DanceValue, f.Danceability);
        SetBar(SpeechFill, SpeechValue, f.Speechiness);
        UpdatePulse();
    }

    void SetBar(ScaleTransform fill, TextBlock value, double? amount)
    {
        value.Text = amount is { } a ? $"{a * 100:0}%" : "—";
        var to = amount ?? 0;
        if (ReduceMotionOn) { fill.BeginAnimation(ScaleTransform.ScaleXProperty, null); fill.ScaleX = to; return; }
        fill.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    bool ReduceMotionOn { get { lock (_settingsLock) return _settings.ReduceMotion; } }

    void UpdatePulse()
    {
        _pulseTimer.Stop();
        if (ReduceMotionOn || _demo || _features.Tempo is not { } bpm || _beatTimer.IsEnabled) return;
        _pulseTimer.Interval = TimeSpan.FromSeconds(60 / bpm);
        _pulseTimer.Start();
    }

    void PulseHeart()
    {
        if (ReduceMotionOn || _features.Tempo is null || PageNow.Visibility != Visibility.Visible) return;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        HeartBeat.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        HeartBeat.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void FlickEars(double rise, double fall)
    {
        if (WindowState == WindowState.Minimized || PageNow.Visibility != Visibility.Visible) return;
        Flick(EarBTurn, 9, 0, rise, fall);
        Flick(EarATurn, -9, 0.03, rise, fall);
        if (_energy is not >= DanceEnergy) return;

        var side = _beatCount++ % 2 == 0 ? 1 : -1;
        Flick(HeadTilt, RotateTransform.AngleProperty, 3 * side, 0, rise, fall);
        Flick(HeadBob, TranslateTransform.YProperty, 3, 0, rise, fall);
        Flick(side > 0 ? PawLUp : PawRUp, TranslateTransform.YProperty, -4, 0.02, rise, fall);
    }

    static void Flick(Animatable target, DependencyProperty property, double to, double delay, double rise, double fall)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(delay) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(rise)), ease));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(rise + fall)), ease));
        target.BeginAnimation(property, a);
    }

    static void Flick(RotateTransform ear, double angle, double delay, double rise, double fall) =>
        Flick(ear, RotateTransform.AngleProperty, angle, delay, rise, fall);

    void SaveSettings()
    {
        try
        {
            lock (_settingsLock) _settings.Save();
            Toast_Show("Saved");
        }
        catch (Exception ex) { Toast_Show($"Couldn't save: {ex.Message}"); }
    }

    void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new Settings();
        lock (_settingsLock)
        {
            _settings.UpdateInterval = defaults.UpdateInterval;
            _settings.LyricsOffset = defaults.LyricsOffset;
            _settings.StatusFormat = defaults.StatusFormat;
            _settings.Emoji = defaults.Emoji;
            _settings.NoLyricsText = defaults.NoLyricsText;
            _settings.PauseText = defaults.PauseText;
            _settings.PausedText = defaults.PausedText;
            _settings.StopOnRateLimit = defaults.StopOnRateLimit;
            _settings.CheckUpdates = defaults.CheckUpdates;
            _settings.JoinFastLines = defaults.JoinFastLines;
            _settings.JoinSeparator = defaults.JoinSeparator;
            _settings.Mascot = defaults.Mascot;
            _settings.EarsDance = defaults.EarsDance;
            _settings.ReduceMotion = defaults.ReduceMotion;
        }
        LoadSettingsIntoForm();
        SaveSettings();
    }

    void Toast_Show(string text)
    {
        ToastText.Text = text;
        var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1800) };
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))));
        Toast.BeginAnimation(OpacityProperty, show);
    }

    void ShowRelease(Release? release)
    {
        _release = release;
        UpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is not null) UpdateText.Text = $"Update to {release.Tag}";
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateResult.Text = "Checking...";
        var release = await Updater.CheckAsync(Http);
        ShowRelease(release);
        UpdateResult.Text = release is null ? "You're on the latest version." : $"{release.Tag} is available — use the button on the left.";
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        if (MessageBox.Show(this, $"Update to {_release.Tag}?\n\nYour accounts and settings are kept.",
                "Lyricat", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Downloading...";
        try
        {
            if (_runTask is { IsCompleted: false }) { _run?.Cancel(); await _runTask; }
            await Updater.ApplyAsync(Http, _release);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = true;
            UpdateText.Text = $"Update to {_release.Tag}";
            MessageBox.Show(this, $"Couldn't update: {ex.Message}", "Lyricat", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public partial class MainWindow
{
    internal void ShowDemo()
    {
        _demo = true;
        UpdateEars();
        SetRunning(true);
        SongTitle.Text = "A Song Title";
        SongArtist.Text = "Some Artist";
        TimeNow.Text = "1:24";
        TimeTotal.Text = "3:31";
        ProgressScale.ScaleX = 0.4;
        string[] sample =
        [
            "sample line number one", "sample line number two", "this line already passed",
            "this one just passed too", "this is the line playing now", "here comes the next one",
            "and one more after that", "a hidden line waiting below", "the last sample line",
        ];
        _carousel.SetSong(sample.Select((t, i) => new LyricLine(i * 3, t)).ToList(), "meow");
        _carousel.MoveTo(4, 4, 5);
        DiscordName.Text = "Kitty";
        DiscordUser.Text = "@kitty";
        DiscordStatus.Text = "this is the line playing now - here comes the next one";
        StatsText.Text = "During this song the status changed 1.2 times every 5 seconds  |  Changes during this session: 48";
        AllTimeText.Text = "All-time status changes: 1,234";
        ShowFeatures(new TrackFeatures(128, 0.84, 0.71, 0.06));
    }

    internal IEnumerable<(Action Show, string Name)> DemoPages() =>
    [
        (() => NavNow.IsChecked = true, "now-playing"),
        (() => NavAccounts.IsChecked = true, "accounts"),
        (() => NavSettings.IsChecked = true, "settings"),
        (() => { NavSettings.IsChecked = true; PageSettings.ScrollToEnd(); }, "settings-bottom"),
        (() => NavAbout.IsChecked = true, "about"),
        (() => { NavNow.IsChecked = true; _demo = true; ShowMascot(Mascots.Dracani); EarBTurn.Angle = 0; EarATurn.Angle = 0; }, "now-playing-dracani"),
        (() => { NavNow.IsChecked = true; _demo = true; ShowMascot(Mascots.Dracani); EarBTurn.Angle = 9; EarATurn.Angle = -9; }, "now-playing-dracani-ears"),
        (() => { NavNow.IsChecked = true; _demo = true; ShowMascot(Mascots.Dracani); EarBTurn.Angle = 9; EarATurn.Angle = -9;
                 HeadTilt.Angle = 3; HeadBob.Y = 3; PawLUp.Y = -4; }, "now-playing-dracani-dance"),
    ];

    internal void DemoAdvance(int index) => _carousel.MoveTo(index, 4, 5);

    internal void ShowDisclaimerForDemo()
    {
        NavNow.IsChecked = true;
        DisclaimerOverlay.Visibility = Visibility.Visible;
        DisclaimerOverlay.Opacity = 1;
    }
}

static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
