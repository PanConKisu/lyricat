using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Shapes = System.Windows.Shapes;
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
            "3. Type 'sp_dc' in the filter box to find it fast.\n4. Copy its value.\n\nUsed to read the lyrics. Never share it."),
        (Secrets.ClientId, "Spotify app Client ID", false,
            "1. Create an app at developer.spotify.com/dashboard.\n2. Tick only 'Web API'.\n" +
            "3. Redirect URI: http://127.0.0.1:8888/callback\n   (the 'Copy redirect URI' button copies it for you)\n4. Copy the Client ID from its settings."),
        (Secrets.ClientSecret, "Spotify app Client Secret", true,
            "In your Spotify app's settings, click 'View client secret' and copy it."),
    ];

    static readonly HttpClient Http = CreateHttp();

    readonly Settings _settings = Settings.Load();
    readonly object _settingsLock = new();
    readonly Dictionary<string, AccountField> _fields = [];
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
    bool _quitting;
    bool _checkedUpdates;
    readonly TrayIcon _tray = new();
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
        IsVisibleChanged += (_, _) => UpdateEars();
        _tray.Open += ShowFromTray;
        _tray.ToggleShare += () =>
        {
            if (!_settings.AcceptedDisclaimer || !Secrets.HasAll()) ShowFromTray();
            ShareToggle_Click(this, new RoutedEventArgs());
        };
        _tray.Quit += Quit;
        Closed += (_, _) => _tray.Dispose();
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
        EnsureLyrics();
        if (!_settings.AcceptedDisclaimer && !SkipDisclaimer) DisclaimerOverlay.Visibility = Visibility.Visible;
        if (!Secrets.HasAll()) ShowMissingGuides();
        await CheckUpdatesOnceAsync();
    }

    void EnsureLyrics()
    {
        if (_lyrics is not null) return;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _lyrics = new SpotifyLyrics(Http, Dispatcher, () => hwnd);
    }

    async Task CheckUpdatesOnceAsync()
    {
        if (_checkedUpdates || !_settings.CheckUpdates || SkipDisclaimer) return;
        _checkedUpdates = true;
        ShowStatus(await Updater.CheckAsync(Http));
    }

    // Started with Windows: stay in the tray and, if everything is set up, start sharing on our own.
    public async void StartInTray()
    {
        if (!_settings.AcceptedDisclaimer || !Secrets.HasAll()) { Show(); return; }
        EnsureLyrics();
        if (_settings.ShareOnStartup) ShareToggle_Click(this, new RoutedEventArgs());
        await CheckUpdatesOnceAsync();
    }

    public void ShowFromTray()
    {
        if (_quitting) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void HideToTray()
    {
        Hide();
        if (_settings.TrayHintShown) return;
        _tray.Tell("Lyricat is here!", "I'll stay in the background to save resources. Right-click this icon if you want me to quit.");
        lock (_settingsLock) _settings.TrayHintShown = true;
        SaveSettingsQuietly();
    }

    public void Quit()
    {
        _quitting = true;
        Close();
    }

    void SaveSettingsQuietly()
    {
        try { lock (_settingsLock) _settings.Save(); } catch (Exception) { }
    }

    void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null and not TextBox and not PasswordBox)
            hit = hit is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        if (hit is null && Keyboard.FocusedElement is TextBox or PasswordBox) Keyboard.ClearFocus();
    }

    void DragArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => HideToTray();
    void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
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
        if (NavAccounts.IsChecked != true) ClearGuides();

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
            ShowMissingGuides();
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
        ShareToggle.Background = (Brush)FindResource(on && !busy ? "PrimarySoft" : "Surface");
        ShareToggle.BorderBrush = (Brush)FindResource(on && !busy ? "Primary" : "Line");
        var track = ((SolidColorBrush)FindResource(busy ? "Questionable" : on ? "Safe" : "Dangerous")).Color;
        ShareTrackColor.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(track, TimeSpan.FromMilliseconds(200)));
        _tray.SetSharing(text, on, busy);
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
                _tempoAnchor = null;
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
                    if (_closing) break;
                    if (IsVisible) MessageBox.Show(this, stopped.Error, "Lyricat", MessageBoxButton.OK, MessageBoxImage.Warning);
                    else _tray.Tell("Lyricat stopped sharing", stopped.Error);
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

    sealed class AccountField
    {
        public required string Key;
        public PasswordBox? Pass;
        public required TextBox Text;
        public required TextBlock Status;
        public required Border Ring;
        public required TextBlock Error;
        public required Canvas Overlay;
        public string Committed = "";
        public bool Edited;
        public int Version;
        public bool Animating;

        public string Value => (Pass is not null && Pass.Visibility == Visibility.Visible ? Pass.Password : Text.Text).Trim();
        public bool Masked => Pass is not null && Pass.Visibility == Visibility.Visible;
    }

    void BuildSecretFields()
    {
        foreach (var (key, label, secret, help) in SecretFieldInfo)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 18, 18) };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var helpIcon = new TextBlock
            {
                Text = "\uE9CE", Style = (Style)FindResource("Icon"), FontSize = 13,
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
            var padding = new Thickness(12, 0, secret ? 64 : 36, 0);
            var grid = new Grid();
            var text = new TextBox { Text = value, Padding = padding };
            PasswordBox? pass = null;
            if (secret)
            {
                pass = new PasswordBox { Password = value, Padding = padding };
                text.Visibility = Visibility.Collapsed;
                grid.Children.Add(pass);
            }
            grid.Children.Add(text);

            var overlay = new Canvas { ClipToBounds = true, IsHitTestVisible = false, Margin = new Thickness(0, 0, secret ? 64 : 36, 0) };
            var ring = new Border
            {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5),
                BorderBrush = (Brush)FindResource("Dangerous"), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            };
            var status = new TextBlock
            {
                Style = (Style)FindResource("Icon"), FontSize = 13, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, secret ? 40 : 14, 0),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(),
            };
            grid.Children.Add(overlay);
            grid.Children.Add(ring);
            grid.Children.Add(status);

            var field = new AccountField
            {
                Key = key, Pass = pass, Text = text, Status = status, Ring = ring, Overlay = overlay,
                Error = new TextBlock
                {
                    FontSize = 11, Foreground = (Brush)FindResource("Dangerous"), Margin = new Thickness(2, 5, 0, 0),
                    Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
                },
                Committed = value,
            };

            if (pass is not null)
            {
                var eye = new Button
                {
                    Style = (Style)FindResource("IconButton"), Content = "\uE890", Width = 30, Height = 28,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0), ToolTip = "Show / hide",
                };
                eye.Click += (_, _) =>
                {
                    var show = text.Visibility != Visibility.Visible;
                    if (show) text.Text = pass.Password; else pass.Password = text.Text;
                    text.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    pass.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                    eye.Content = show ? "\uED1A" : "\uE890";
                };
                grid.Children.Add(eye);
                eye.Focusable = false;
                pass.LostKeyboardFocus += (_, _) => _ = CommitAsync(field);
                pass.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = CommitAsync(field); };
                pass.PasswordChanged += (_, _) => ClearCheck(field);
            }
            text.LostKeyboardFocus += (_, _) => _ = CommitAsync(field);
            text.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = CommitAsync(field); };
            text.TextChanged += (_, _) => ClearCheck(field);

            panel.Children.Add(grid);
            panel.Children.Add(field.Error);
            SecretFields.Children.Add(panel);
            _fields[key] = field;
        }
    }

    void ClearCheck(AccountField field)
    {
        if (_loading) return;
        if (field.Value.Length > 0) HideGuide(field.Key);
        field.Edited = true;
        field.Version++;
        field.Ring.Visibility = Visibility.Collapsed;
        field.Error.Visibility = Visibility.Collapsed;
        SetStatus(field, null);
    }

    void SetStatus(AccountField field, bool? ok)
    {
        var turn = (RotateTransform)field.Status.RenderTransform;
        turn.BeginAnimation(RotateTransform.AngleProperty, null);
        turn.Angle = 0;
        switch (ok)
        {
            case true:
                field.Status.Text = "\uE73E";
                field.Status.Foreground = (Brush)FindResource("Safe");
                break;
            case false:
                field.Status.Text = "\uE711";
                field.Status.Foreground = (Brush)FindResource("Dangerous");
                break;
            default:
                field.Status.Text = "";
                break;
        }
    }

    void SetChecking(AccountField field)
    {
        field.Status.Text = "\uE72C";
        field.Status.Foreground = (Brush)FindResource("Muted");
        ((RotateTransform)field.Status.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    void ShowResult(AccountField field, CheckResult result)
    {
        SetStatus(field, result.Ok);
        field.Ring.Visibility = result.Ok ? Visibility.Collapsed : Visibility.Visible;
        field.Error.Text = result.Error ?? "";
        field.Error.Visibility = result.Ok ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task CommitAsync(AccountField field)
    {
        var value = field.Value;
        if (value.Length == 0 || !field.Edited) return;
        await Task.Delay(350);
        if (_saving || !field.Edited) return;
        field.Edited = false;
        field.Committed = value;
        var (result, _) = await CheckAsync(field.Key);
        if (result is null || field.Value != value) return;
        if (result.Ok && field.Masked && Greeting(field.Key, result) is { } hello) await HelloAsync(field, hello);
    }

    static string? Greeting(string key, CheckResult result) => key switch
    {
        Secrets.DiscordToken => result.Alias is { } name ? $"Hello, {name}!" : "Hello there!",
        Secrets.SpDc => result.Alias is { } name ? $"Hemlo, {name}!" : "Hemlo!",
        Secrets.ClientSecret => "Your Spotify app is ready!",
        _ => null,
    };

    async Task<(CheckResult? Result, AccountField[] Fields)> CheckAsync(string key)
    {
        AccountField[] involved = key is Secrets.ClientId or Secrets.ClientSecret
            ? [_fields[Secrets.ClientId], _fields[Secrets.ClientSecret]]
            : [_fields[key]];
        if (involved.Any(f => f.Value.Length == 0)) return (null, involved);

        var versions = involved.Select(f => ++f.Version).ToArray();
        foreach (var f in involved) SetChecking(f);

        var result = key switch
        {
            Secrets.DiscordToken => await AccountCheck.DiscordAsync(Http, involved[0].Value),
            Secrets.SpDc => await AccountCheck.SpotifyCookieAsync(CheckerSpotify(), involved[0].Value),
            _ => await AccountCheck.SpotifyAppAsync(Http, involved[0].Value, involved[1].Value),
        };

        for (var i = 0; i < involved.Length; i++)
            if (involved[i].Version == versions[i]) ShowResult(involved[i], result);
        if (result.Discord is { } me)
        {
            DiscordName.Text = me.DisplayName;
            DiscordUser.Text = "@" + me.Username;
            LoadImage(me.AvatarUrl, img => Avatar.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill });
        }
        return (result, involved);
    }

    SpotifyLyrics? _checker;
    SpotifyLyrics CheckerSpotify()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return _checker ??= new SpotifyLyrics(Http, Dispatcher, () => hwnd);
    }

    async Task HelloAsync(AccountField field, string message)
    {
        if (field.Animating || field.Pass is not { } pass) return;
        field.Animating = true;
        var backdrop = AddBackdrop(pass);
        try
        {
            var reduce = ReduceMotionOn;
            var clear = new GradientStop(Colors.Transparent, -0.25);
            var solid = new GradientStop(Colors.Black, 0);
            pass.OpacityMask = new LinearGradientBrush(new GradientStopCollection { clear, solid }, new Point(0, 0.5), new Point(1, 0.5));

            Task Sweep(double from, double to, int ms)
            {
                var done = new TaskCompletionSource();
                var duration = TimeSpan.FromMilliseconds(reduce ? 1 : ms);
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                var a = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
                a.Completed += (_, _) => done.TrySetResult();
                clear.BeginAnimation(GradientStop.OffsetProperty, a);
                solid.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(from + 0.25, to + 0.25, duration) { EasingFunction = ease });
                return done.Task;
            }

            await Sweep(-0.25, 1, 450);

            var hello = new TextBlock
            {
                Text = message, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Deep"), Opacity = 0,
            };
            hello.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetTop(hello, Math.Max(0, (field.Overlay.ActualHeight - hello.DesiredSize.Height) / 2));
            field.Overlay.Children.Add(hello);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slideIn = TimeSpan.FromMilliseconds(reduce ? 1 : 520);
            hello.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(field.Overlay.ActualWidth, 12, slideIn) { EasingFunction = ease });
            hello.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, slideIn));
            await Task.Delay(slideIn + TimeSpan.FromMilliseconds(1400));

            var slideOut = TimeSpan.FromMilliseconds(reduce ? 1 : 420);
            hello.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(12, -hello.DesiredSize.Width * 0.4, slideOut)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
            hello.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, slideOut));
            await Sweep(1, -0.25, 520);
            field.Overlay.Children.Remove(hello);
        }
        finally
        {
            pass.OpacityMask = null;
            ((Grid)pass.Parent).Children.Remove(backdrop);
            field.Animating = false;
        }
    }

    Border AddBackdrop(PasswordBox pass)
    {
        var backdrop = new Border
        {
            CornerRadius = new CornerRadius(12), IsHitTestVisible = false,
            Background = (Brush)FindResource(pass.IsKeyboardFocused ? "Surface" : "SurfaceSoft"),
        };
        ((Grid)pass.Parent).Children.Insert(0, backdrop);
        return backdrop;
    }

    bool _saving;

    async void SaveAccounts_Click(object sender, RoutedEventArgs e)
    {
        _saving = true;
        try { await SaveAccountsAsync(); }
        finally { _saving = false; }
    }

    async Task SaveAccountsAsync()
    {
        foreach (var field in _fields.Values)
        {
            Secrets.Set(field.Key, field.Value);
            field.Committed = field.Value;
            field.Edited = false;
        }
        var missing = SecretFieldInfo.Where(f => Secrets.Get(f.Key).Length == 0).Select(f => f.Label).ToList();
        AccountsResult.Text = missing.Count > 0 ? "Saved. Still missing: " + string.Join(", ", missing) + "." : "Saved. Checking...";
        if (missing.Count > 0) ShowMissingGuides();

        var checks = await Task.WhenAll(
            CheckAsync(Secrets.DiscordToken), CheckAsync(Secrets.SpDc), CheckAsync(Secrets.ClientSecret));
        foreach (var (result, fields) in checks)
            if (result is { Ok: true })
                foreach (var field in fields)
                    if (field.Masked && Greeting(field.Key, result) is { } hello) _ = HelloAsync(field, hello);
        if (missing.Count > 0) return;
        AccountsResult.Text = checks.All(c => c.Result is { Ok: true })
            ? "Saved. Everything checks out!"
            : "Saved, but something didn't check out: see the fields marked in red.";
    }

    readonly Dictionary<string, UIElement[]> _guides = [];
    TextBlock? _guideText;

    void ShowMissingGuides()
    {
        NavAccounts.IsChecked = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ClearGuides();
            var missing = _fields.Values.Where(f => f.Value.Length == 0).ToList();
            if (missing.Count == 0) return;
            for (var i = 0; i < missing.Count; i++) DrawArrow(missing[i], i * 0.14);
            ShowGuideText(missing.Count == 1 ? "This one is missing!" : "These are missing!", 0.3);
        });
    }

    void ClearGuides()
    {
        foreach (var key in _guides.Keys.ToList()) HideGuide(key);
        if (_guideText is not null) Guides.Children.Remove(_guideText);
        _guideText = null;
    }

    void HideGuide(string key)
    {
        if (!_guides.Remove(key, out var parts)) return;
        foreach (var part in parts)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => Guides.Children.Remove(part);
            part.BeginAnimation(OpacityProperty, fade);
        }
    }

    bool Instant => ReduceMotionOn || _demo;

    // A slightly arched, whiteboard-style arrow that draws itself from tail to tip.
    void DrawArrow(AccountField field, double delay)
    {
        var box = (FrameworkElement)field.Text.Parent;
        var origin = box.TransformToVisual(Guides).Transform(new Point());
        var tip = new Point(origin.X + box.ActualWidth * 0.6, origin.Y - 3);
        Point At(double x, double y) => new(tip.X + x * 0.78, tip.Y + y * 0.78);
        var segments = new[] { (At(86, -34), At(66, -56), At(20, -48), tip) };
        var tail = segments[0].Item1;
        var c2 = segments[^1].Item3;

        var figure = new PathFigure { StartPoint = tail };
        foreach (var (_, a, b, end) in segments) figure.Segments.Add(new BezierSegment(a, b, end, true));
        var brush = (Brush)FindResource("Primary");
        const double thickness = 2.5;
        var body = new Shapes.Path
        {
            Data = new PathGeometry([figure]), Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };

        var direction = tip - c2;
        direction.Normalize();
        static Vector Turn(Vector v, double degrees)
        {
            var r = degrees * Math.PI / 180;
            return new Vector(v.X * Math.Cos(r) - v.Y * Math.Sin(r), v.X * Math.Sin(r) + v.Y * Math.Cos(r));
        }
        var head = new Shapes.Path
        {
            Data = new PathGeometry([new PathFigure(tip - Turn(direction, 32) * 9,
                [new LineSegment(tip, true), new LineSegment(tip - Turn(direction, -32) * 9, true)], false)]),
            Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0, 0), RenderTransform = new ScaleTransform(1, 1, tip.X, tip.Y),
        };
        Guides.Children.Add(body);
        Guides.Children.Add(head);
        _guides[field.Key] = [body, head];
        if (Instant) return;

        double length = 0;
        foreach (var (p0, a, b, p1) in segments)
        {
            var last = p0;
            for (var i = 1; i <= 40; i++)
            {
                var t = i / 40.0;
                var u = 1 - t;
                var p = new Point(
                    u * u * u * p0.X + 3 * u * u * t * a.X + 3 * u * t * t * b.X + t * t * t * p1.X,
                    u * u * u * p0.Y + 3 * u * u * t * a.Y + 3 * u * t * t * b.Y + t * t * t * p1.Y);
                length += (p - last).Length;
                last = p;
            }
        }
        var dash = length / thickness + 1;
        body.StrokeDashArray = [dash, dash];
        body.StrokeDashOffset = dash;
        body.BeginAnimation(Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(dash, 0, TimeSpan.FromMilliseconds(700))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        head.Opacity = 0;
        var headAt = TimeSpan.FromSeconds(delay + 0.64);
        head.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { BeginTime = headAt });
        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = headAt, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        head.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        head.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // Peeks in, bounces a little (smaller, bigger, bigger, smaller, a bit bigger), then fades away.
    void ShowGuideText(string message, double delay)
    {
        var text = new TextBlock
        {
            Text = message, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Deep"), RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(1, 1);
        text.RenderTransform = scale;
        var anchor = AccountsResult.TransformToVisual(Guides).Transform(new Point());
        Canvas.SetLeft(text, anchor.X);
        Canvas.SetTop(text, anchor.Y + 26);
        Guides.Children.Add(text);
        _guideText = text;
        if (Instant) return;

        var start = TimeSpan.FromSeconds(delay);
        var bounce = new DoubleAnimationUsingKeyFrames { BeginTime = start };
        void Key(double value, int ms, IEasingFunction ease) =>
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), ease));
        var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var inOut = new SineEase { EasingMode = EasingMode.EaseInOut };
        Key(0, 0, inOut);
        Key(1.14, 320, outEase);
        Key(0.93, 450, inOut);
        Key(1.07, 580, inOut);
        Key(1.12, 680, inOut);
        Key(0.96, 820, inOut);
        Key(1.04, 960, inOut);
        Key(1.04, 2700, inOut);
        Key(0.7, 3050, new CubicEase { EasingMode = EasingMode.EaseIn });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);

        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = start };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2700))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3050))));
        fade.Completed += (_, _) =>
        {
            Guides.Children.Remove(text);
            if (_guideText == text) _guideText = null;
        };
        text.BeginAnimation(OpacityProperty, fade);
    }

    void OpenDashboard_Click(object sender, RoutedEventArgs e) => Open("https://developer.spotify.com/dashboard");

    void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SpotifyApi.RedirectUri);
            Toast_Show("Redirect URI copied!");
        }
        catch (Exception) { Toast_Show("Couldn't copy it, try again"); }
    }
    void OpenRepo_Click(object sender, RoutedEventArgs e) => Open(Updater.RepoUrl);
    void OpenDracani_Click(object sender, RoutedEventArgs e) => Open("https://linktr.ee/Dracani");
    void OpenKofi_Click(object sender, RoutedEventArgs e) => Open("https://ko-fi.com/panconkisu");

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
        StartWithWindows.IsChecked = AutoStart.IsEnabled;
        ShareOnStartup.IsChecked = _settings.ShareOnStartup;
        ShowStartup();
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
            _settings.ShareOnStartup = ShareOnStartup.IsChecked == true;
        }
        ShowMascot(_settings.Mascot);
        UpdatePulse();
        ShowSeparator();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            AutoStart.Set(StartWithWindows.IsChecked == true);
            Toast_Show(StartWithWindows.IsChecked == true ? "Lyricat will start with Windows" : "Lyricat won't start with Windows");
        }
        catch (Exception ex)
        {
            Toast_Show($"Couldn't change it: {ex.Message}");
            _loading = true;
            StartWithWindows.IsChecked = AutoStart.IsEnabled;
            _loading = false;
        }
        ShowStartup();
    }

    void ShowStartup()
    {
        var on = StartWithWindows.IsChecked == true;
        ShareOnStartup.IsEnabled = on;
        ShareOnStartup.Opacity = on ? 1 : 0.45;
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
            want = !_demo && _settings.Mascot == Mascots.Dracani && _settings.EarsDance && IsVisible && WindowState != WindowState.Minimized;
        if (want == _beatTimer.IsEnabled) return;
        if (want) { _clock.Reset(); _beats.Start(); _beatTimer.Start(); }
        else { _beats.Stop(); _beatTimer.Stop(); }
        UpdatePulse();
    }

    double? _tempoAnchor;

    // Nothing heard (Spotify playing on another device, or the audio capture failed):
    // keep dancing at Spotify's tempo, just without knowing where the beats land.
    BeatGrid? TempoOnlyGrid()
    {
        if (_features.Tempo is not { } bpm || _playback is not { IsPlaying: true }) return null;
        while (bpm > 160) bpm /= 2;
        while (bpm < 60) bpm *= 2;
        _tempoAnchor ??= BeatListener.Now;
        return new BeatGrid(60 / bpm, _tempoAnchor.Value, 0);
    }

    void BeatTick()
    {
        var grid = _beats.Grid ?? TempoOnlyGrid();
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
            _settings.ShareOnStartup = defaults.ShareOnStartup;
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

    void ShowStatus(UpdateStatus? status)
    {
        if (status is null) return;
        _release = status.Newer;
        UpdateButton.Visibility = status.Newer is null ? Visibility.Collapsed : Visibility.Visible;
        if (status.Newer is not null) UpdateText.Text = $"Update to {status.Newer.Tag}";

        var latest = status.Behind == 0;
        AboutStatus.Text = latest
            ? "  ·  You are running the latest version, YIPEEEE!!!!1"
            : $"  ·  You're {status.Behind} version{(status.Behind == 1 ? "" : "s")} behind!";
        AboutStatus.Foreground = (Brush)FindResource(latest ? "Safe" : "Questionable");
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateResult.Text = "Checking...";
        var status = await Updater.CheckAsync(Http);
        ShowStatus(status);
        UpdateResult.Text = status switch
        {
            null => "Couldn't reach GitHub, try again in a bit.",
            { Newer: { } newer } => $"{newer.Tag} is available, use the button on the left.",
            _ => "",
        };
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
            _quitting = true;
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
        ShowStatus(new UpdateStatus(null, 0));
        ShowFeatures(new TrackFeatures(128, 0.84, 0.71, 0.06));
    }

    internal IEnumerable<(Action Show, string Name)> DemoPages() =>
    [
        (() => NavNow.IsChecked = true, "now-playing"),
        (() => { NavAccounts.IsChecked = true; DemoAccounts(); }, "accounts"),
        (() => { DemoMissing(); }, "accounts-missing"),
        (() => NavSettings.IsChecked = true, "settings"),
        (() => { NavSettings.IsChecked = true; PageSettings.ScrollToEnd(); }, "settings-bottom"),
        (() => NavAbout.IsChecked = true, "about"),
        (() => { NavNow.IsChecked = true; SetShareState("Not sharing", on: false, busy: false); }, "share-off"),
        (() => SetShareState("Starting...", on: true, busy: true), "share-starting"),
        (() => SetShareState("Sharing", on: true, busy: false), "share-on"),
        (() => { NavAbout.IsChecked = true; PageAbout.ScrollToVerticalOffset(330); }, "about-version"),
        (() => { NavNow.IsChecked = true; _demo = true; ShowMascot(Mascots.Dracani); EarBTurn.Angle = 0; EarATurn.Angle = 0; }, "now-playing-dracani"),
        (() => { NavNow.IsChecked = true; _demo = true; ShowMascot(Mascots.Dracani); EarBTurn.Angle = 9; EarATurn.Angle = -9; }, "now-playing-dracani-ears"),
        (() => { NavNow.IsChecked = true; _demo = true; ShowMascot(Mascots.Dracani); EarBTurn.Angle = 9; EarATurn.Angle = -9;
                 HeadTilt.Angle = 3; HeadBob.Y = 3; PawLUp.Y = -4; }, "now-playing-dracani-dance"),
    ];

    void DemoMissing()
    {
        DemoAccounts();
        foreach (var f in _fields.Values)
        {
            f.Ring.Visibility = f.Error.Visibility = Visibility.Collapsed;
            f.Status.Text = "";
            f.Pass?.ClearValue(UIElement.OpacityMaskProperty);
            f.Overlay.Children.Clear();
        }
        _fields[Secrets.SpDc].Pass!.Password = "";
        _fields[Secrets.ClientSecret].Pass!.Password = "";
        UpdateLayout();
        ClearGuides();
        DrawArrow(_fields[Secrets.SpDc], 0);
        DrawArrow(_fields[Secrets.ClientSecret], 0);
        ShowGuideText("These are missing!", 0);
    }

    void DemoAccounts()
    {
        foreach (var f in _fields.Values)
        {
            f.Text.Text = f.Key == Secrets.ClientId ? "0123456789abcdef0123456789abcdef" : "";
            if (f.Pass is not null) f.Pass.Password = new string('x', 40);
        }
        ShowResult(_fields[Secrets.DiscordToken], new CheckResult(true, "Kitty", null));
        ShowResult(_fields[Secrets.SpDc], new CheckResult(false, null, "An invalid cookie was provided."));
        ShowResult(_fields[Secrets.ClientId], new CheckResult(true, null, null));
        var secret = _fields[Secrets.ClientSecret];
        SetChecking(secret);
        ((RotateTransform)secret.Status.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
        AddBackdrop(secret.Pass!);
        secret.Pass!.OpacityMask = new LinearGradientBrush(new GradientStopCollection
            { new(Colors.Transparent, 0.55), new(Colors.Black, 0.8) }, new Point(0, 0.5), new Point(1, 0.5));
        secret.Overlay.Children.Add(new TextBlock
        {
            Text = "Your Spotify app is ready!", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Deep"), Margin = new Thickness(12, 11, 0, 0),
        });
    }

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
