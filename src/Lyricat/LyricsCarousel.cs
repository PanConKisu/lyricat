using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LyricsStatus.Services;

namespace LyricsStatus;

public sealed class LyricsCarousel
{
    const int Slots = 6;
    static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(420);
    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

    static double TopOf(int role) => role switch
    {
        <= -3 => -36, -2 => 0, -1 => 34, 0 => 70, 1 => 116, 2 => 150, _ => 186,
    };

    static double SizeOf(int role) => Math.Abs(role) switch { 0 => 22, 1 => 15, _ => 14 };
    static double OpacityOf(int role) => Math.Abs(role) switch { 0 => 1, 1 => 0.8, 2 => 0.45, _ => 0 };

    readonly Canvas _canvas;
    readonly TextBlock[] _blocks = new TextBlock[Slots];
    readonly int[] _roles = new int[Slots];
    readonly Color _deep, _muted;
    readonly Func<string> _pauseText;

    IReadOnlyList<LyricLine> _lines = [];
    string _fallback = "";
    int _index = -1;
    int _groupStart = -1, _groupEnd = -1;

    public LyricsCarousel(Canvas canvas, Color deep, Color muted, FontFamily font, Func<string> pauseText)
    {
        _canvas = canvas;
        _deep = deep;
        _muted = muted;
        _pauseText = pauseText;
        for (var i = 0; i < Slots; i++)
        {
            _blocks[i] = new TextBlock
            {
                FontFamily = font,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(muted),
            };
            canvas.Children.Add(_blocks[i]);
        }
        canvas.SizeChanged += (_, _) =>
        {
            foreach (var b in _blocks) b.Width = Math.Max(0, canvas.ActualWidth);
        };
        Reset();
    }

    public void SetSong(IReadOnlyList<LyricLine> lines, string fallback)
    {
        _lines = lines;
        _fallback = fallback;
        _index = _groupStart = _groupEnd = -1;
        Reset(fade: true);
    }

    public void SetFallback(string fallback)
    {
        if (_fallback == fallback) return;
        _fallback = fallback;
        if (_lines.Count == 0) Reset();
    }

    public void MoveTo(int index, int groupStart, int groupEnd)
    {
        var groupChanged = groupStart != _groupStart || groupEnd != _groupEnd;
        _groupStart = groupStart;
        _groupEnd = groupEnd;

        if (index == _index)
        {
            if (groupChanged)
                for (var s = 0; s < Slots; s++) Apply(s, animate: true);
            return;
        }
        if (index == _index + 1) Advance(index);
        else { _index = index; Reset(fade: true); }
    }

    public void MoveTo(int index) => MoveTo(index, index, index);

    bool Highlighted(int role)
    {
        var line = _index + role;
        return role == 0 || (line >= 0 && line >= _groupStart && line <= _groupEnd);
    }

    string TextAt(int i)
    {
        if (_lines.Count == 0) return i == _index ? _fallback : "";
        if (i < 0 || i >= _lines.Count) return i == _index ? _pauseText() : "";
        return Engine.IsInstrumental(_lines[i].Text) ? _pauseText() : _lines[i].Text;
    }

    void Reset(bool fade = false)
    {
        for (var s = 0; s < Slots; s++)
        {
            _roles[s] = s - 2;
            _blocks[s].Text = TextAt(_index + _roles[s]);
            Apply(s, animate: false);
        }
        if (fade)
            _canvas.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    void Advance(int index)
    {
        for (var s = 0; s < Slots; s++)
        {
            if (_roles[s] > -3) continue;
            _roles[s] = 3;
            _blocks[s].Text = TextAt(_index + 3);
            Apply(s, animate: false);
        }

        _index = index;
        for (var s = 0; s < Slots; s++)
        {
            _roles[s]--;
            Apply(s, animate: true);
        }
    }

    void Apply(int slot, bool animate)
    {
        var block = _blocks[slot];
        var role = _roles[slot];
        var partner = role != 0 && Math.Abs(role) == 1 && Highlighted(role);
        block.FontWeight = role == 0 || partner ? FontWeights.SemiBold : FontWeights.Normal;
        var color = role == 0 || partner ? _deep : _muted;
        var size = partner ? 19 : SizeOf(role);
        var opacity = partner ? 1 : OpacityOf(role);

        if (!animate)
        {
            block.BeginAnimation(Canvas.TopProperty, null);
            block.BeginAnimation(UIElement.OpacityProperty, null);
            block.BeginAnimation(TextBlock.FontSizeProperty, null);
            Canvas.SetTop(block, TopOf(role));
            block.Opacity = opacity;
            block.FontSize = size;
            block.Foreground = new SolidColorBrush(color);
            return;
        }

        DoubleAnimation To(double value) => new(value, Duration) { EasingFunction = Ease };
        block.BeginAnimation(Canvas.TopProperty, To(TopOf(role)));
        block.BeginAnimation(UIElement.OpacityProperty, To(opacity));
        block.BeginAnimation(TextBlock.FontSizeProperty, To(size));
        var brush = new SolidColorBrush(((SolidColorBrush)block.Foreground).Color);
        block.Foreground = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, Duration) { EasingFunction = Ease });
    }
}
