using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LyricsStatus.Services;

public sealed record BeatGrid(double Period, double Anchor, double Confidence)
{
    public double Bpm => 60 / Period;
}

public sealed class BeatTracker
{
    const double Fps = 100;
    const int History = 600;
    const double MinBpm = 70, MaxBpm = 180;

    readonly int _hop;
    readonly double[] _env = new double[History];
    int _frames;
    double _lastFrameTime;

    readonly double _b0, _b1, _b2, _a1, _a2;
    double _x1, _x2, _y1, _y2;
    int _inHop;
    double _lowSum, _fullSum, _prevLow, _prevFull;

    double _period;
    double _hintBpm;
    public BeatGrid? Grid { get; private set; }

    public double? TempoHint
    {
        get => Volatile.Read(ref _hintBpm) is > 0 and var v ? v : null;
        set => Volatile.Write(ref _hintBpm, value ?? 0);
    }

    public BeatTracker(int sampleRate)
    {
        _hop = Math.Max(1, (int)Math.Round(sampleRate / Fps));
        var w = 2 * Math.PI * 150 / sampleRate;
        var alpha = Math.Sin(w) / (2 * 0.707);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        _b0 = (1 - cos) / 2 / a0; _b1 = (1 - cos) / a0; _b2 = _b0;
        _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
    }

    public void Process(ReadOnlySpan<float> mono, double endTime, double sampleRate)
    {
        for (var i = 0; i < mono.Length; i++)
        {
            double x = mono[i];
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            _lowSum += y * y;
            _fullSum += x * x;
            if (++_inHop < _hop) continue;

            var low = Math.Log(_lowSum / _hop + 1e-7);
            var full = Math.Log(_fullSum / _hop + 1e-7);
            var silent = _fullSum / _hop < 1e-7;
            var onset = silent ? 0 : Math.Max(0, low - _prevLow) + 0.5 * Math.Max(0, full - _prevFull);
            _prevLow = low; _prevFull = full;
            _lowSum = _fullSum = 0; _inHop = 0;

            _env[_frames % History] = onset;
            _frames++;
            _lastFrameTime = endTime - (mono.Length - 1 - i) / sampleRate;
            if (_frames % 25 == 0 && _frames >= 300) Analyze();
        }
    }

    public void Reset()
    {
        Array.Clear(_env);
        _frames = 0;
        _period = 0;
        Grid = null;
    }

    void Analyze()
    {
        var n = Math.Min(_frames, History);
        var e = new double[n];
        for (var i = 0; i < n; i++) e[i] = _env[(_frames - n + i) % History];
        var mean = e.Average();
        for (var i = 0; i < n; i++) e[i] = Math.Max(0, e[i] - mean);

        double zero = 0;
        for (var i = 0; i < n; i++) zero += e[i] * e[i];
        if (zero < 1e-9) { Grid = null; return; }

        double confidence;
        if (TempoHint is { } hint)
        {
            while (hint > 160) hint /= 2;
            while (hint < 60) hint *= 2;
            _period = 60 / hint;
            confidence = 1;
        }
        else if (EstimatePeriod(e, n, zero) is { } estimate)
        {
            (_period, confidence) = estimate;
        }
        else { Grid = null; return; }

        var p = _period * Fps;
        int bestOffset = 0; double bestPhase = -1;
        for (var offset = 0; offset < (int)Math.Ceiling(p); offset++)
        {
            double s = 0; var k = 0;
            for (var pos = n - 1 - offset; pos >= 0; pos = n - 1 - offset - (int)Math.Round(++k * p))
                s += e[pos] * (1 - 0.06 * k);
            if (s > bestPhase) { bestPhase = s; bestOffset = offset; }
        }
        Grid = new BeatGrid(_period, _lastFrameTime - bestOffset / Fps, confidence);
    }

    (double Period, double Confidence)? EstimatePeriod(double[] e, int n, double zero)
    {
        int minLag = (int)(Fps * 60 / MaxBpm), maxLag = (int)Math.Ceiling(Fps * 60 / MinBpm);
        var ac = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double s = 0;
            for (var i = lag; i < n; i++) s += e[i] * e[i - lag];
            ac[lag] = s / (n - lag);
        }
        int best = -1; double bestScore = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            if (ac[lag] < ac[lag - 1] || ac[lag] < ac[lag + 1]) continue;
            var bpm = Fps * 60 / lag;
            var weight = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.8, 2));
            if (ac[lag] * weight > bestScore) { bestScore = ac[lag] * weight; best = lag; }
        }
        if (best < 0) return null;

        double l = best, y0 = ac[best - 1], y1 = ac[best], y2 = ac[best + 1];
        var denom = y0 - 2 * y1 + y2;
        if (Math.Abs(denom) > 1e-12) l += 0.5 * (y0 - y2) / denom;
        var period = l / Fps;
        var confidence = ac[best] / (zero / n);

        var smoothed = _period > 0 && Math.Abs(period - _period) / _period < 0.06
            ? _period * 0.8 + period * 0.2
            : period;
        return (smoothed, confidence);
    }
}

public sealed class BeatListener : IDisposable
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;

    WasapiLoopbackCapture? _capture;
    BeatTracker? _tracker;
    float[] _mono = [];
    double _rate;
    double _quietSince = -1;
    volatile BeatGrid? _grid;
    bool _wanted;

    double? _tempo;

    public BeatGrid? Grid => _grid;

    public void SetTempo(double? bpm)
    {
        _tempo = bpm;
        if (_tracker is { } t) t.TempoHint = bpm;
    }
    public BeatGrid? RawGrid => _tracker?.Grid;
    public event Action<string>? Log;
    public event Action? Lost;

    public void Start()
    {
        _wanted = true;
        if (_capture is not null) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            var format = capture.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat && format.Encoding != WaveFormatEncoding.Extensible
                || format.BitsPerSample != 32)
            {
                capture.Dispose();
                Log?.Invoke($"Ears: unsupported audio format ({format}).");
                return;
            }
            _rate = format.SampleRate;
            _tracker = new BeatTracker(format.SampleRate) { TempoHint = _tempo };
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            capture.StartRecording();
        }
        catch (Exception e)
        {
            _capture = null;
            Log?.Invoke($"Ears: couldn't listen to the audio output ({e.Message}).");
        }
    }

    public void Stop()
    {
        _wanted = false;
        var capture = _capture;
        _capture = null;
        _grid = null;
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try { capture.StopRecording(); } catch (Exception) { }
        capture.Dispose();
    }

    public void Restart()
    {
        if (!_wanted) return;
        Stop();
        Start();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var tracker = _tracker;
        if (tracker is null || sender is not WasapiCapture capture) return;
        var channels = capture.WaveFormat.Channels;
        var samples = MemoryMarshalCast(e.Buffer, e.BytesRecorded);
        var frames = samples.Length / channels;
        if (_mono.Length < frames) _mono = new float[frames];
        double peak = 0;
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += samples[f * channels + c];
            var v = sum / channels;
            _mono[f] = v;
            if (Math.Abs(v) > peak) peak = Math.Abs(v);
        }
        var now = Now;
        tracker.Process(_mono.AsSpan(0, frames), now, _rate);

        if (peak < 0.003) { if (_quietSince < 0) _quietSince = now; }
        else _quietSince = -1;
        if (_quietSince >= 0 && now - _quietSince > 2) { tracker.Reset(); _grid = null; return; }

        _grid = tracker.Grid is { Confidence: > 0.25 } g ? g : null;
    }

    static ReadOnlySpan<float> MemoryMarshalCast(byte[] buffer, int bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes - bytes % 4));

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        Log?.Invoke($"Ears: audio capture stopped ({e.Exception.Message}).");
        Lost?.Invoke();
    }

    public void Dispose() => Stop();
}

public sealed class BeatClock
{
    double _period, _next = double.NaN;
    int _offBeats;

    public void Reset() => _next = double.NaN;

    public double? Tick(double now, BeatGrid? grid, double lead, double latency)
    {
        if (grid is null) { _next = double.NaN; return null; }
        var origin = grid.Anchor + latency;

        if (double.IsNaN(_next) || Math.Abs(grid.Period - _period) / _period > 0.04 || now > _next + _period)
        {
            _period = grid.Period;
            _next = origin + Math.Ceiling((now - origin) / _period) * _period;
            _offBeats = 0;
        }
        if (now < _next - lead) return null;

        var beat = _next;
        var error = origin - beat;
        error -= Math.Round(error / grid.Period) * grid.Period;
        if (Math.Abs(error) < 0.25 * _period) { _next += 0.2 * error; _offBeats = 0; }
        else if (++_offBeats >= 4) { _next += error; _offBeats = 0; }
        _period += 0.1 * (grid.Period - _period);
        _next += _period;
        return beat;
    }
}
