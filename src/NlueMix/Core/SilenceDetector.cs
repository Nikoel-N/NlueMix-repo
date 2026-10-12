namespace NlueMix.Core;

/// <summary>
/// 静音/有声检测。手机放歌时用来判断"该让路了 / 可以让回来了"。
/// 用 RMS + 峰值双阈值，并且要求持续一段时间，避免误触发。
/// </summary>
public sealed class SilenceDetector
{
    private readonly float _loudThreshold;
    private readonly float _quietThreshold;
    private readonly int _attackMs;
    private readonly int _releaseMs;

    private double _windowSumSquares;
    private int _windowSamples;
    private float _windowPeak;
    private double _lastWindowMs;

    private double _loudAccumMs;
    private double _quietAccumMs;

    /// <param name="loudThresholdDb">判定"有声"的门槛</param>
    /// <param name="quietThresholdDb">判定"无声"的门槛（要比上面低，形成迟滞）</param>
    /// <param name="attackMs">连续多久有声才认为真的在播放</param>
    /// <param name="releaseMs">连续多久无声才认为播完了</param>
    public SilenceDetector(
        double loudThresholdDb = -42,
        double quietThresholdDb = -50,
        int attackMs = 320,
        int releaseMs = 1800)
    {
        _loudThreshold = DbToLinear(loudThresholdDb);
        _quietThreshold = DbToLinear(quietThresholdDb);
        _attackMs = attackMs;
        _releaseMs = releaseMs;
    }

    public bool IsLoud { get; private set; }
    public double LastLevelDb { get; private set; } = -96;
    public double PeakDb { get; private set; } = -96;

    private static float DbToLinear(double db) => (float)Math.Pow(10, db / 20.0);

    /// <summary>喂入一帧音频（交错多声道）。</summary>
    public void Process(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        if (samples.Length == 0) return;

        foreach (float s in samples)
        {
            _windowSumSquares += (double)s * s;
            _windowSamples++;
            float abs = MathF.Abs(s);
            if (abs > _windowPeak) _windowPeak = abs;
        }

        double frames = samples.Length / (double)channels;
        double windowMs = frames * 1000.0 / sampleRate;
        _lastWindowMs += windowMs;

        // 每 20 ms 结算一次
        if (_lastWindowMs < 20) return;

        double rms = _windowSamples > 0 ? Math.Sqrt(_windowSumSquares / _windowSamples) : 0;
        _windowSumSquares = 0;
        _windowSamples = 0;

        LastLevelDb = rms > 0 ? 20 * Math.Log10(rms) : -96;
        PeakDb = _windowPeak > 0 ? 20 * Math.Log10(_windowPeak) : -96;
        _windowPeak = 0;

        bool loud = rms >= _loudThreshold;
        bool quiet = rms <= _quietThreshold;

        if (loud) { _loudAccumMs += _lastWindowMs; _quietAccumMs = 0; }
        else if (quiet) { _quietAccumMs += _lastWindowMs; _loudAccumMs = 0; }
        else { _loudAccumMs = 0; _quietAccumMs = 0; }

        if (!IsLoud && _loudAccumMs >= _attackMs) IsLoud = true;
        else if (IsLoud && _quietAccumMs >= _releaseMs) IsLoud = false;

        _lastWindowMs = 0;
    }

    /// <summary>数据流断开时强制复位。</summary>
    public void Reset()
    {
        IsLoud = false;
        _loudAccumMs = 0;
        _quietAccumMs = 0;
        _windowSumSquares = 0;
        _windowSamples = 0;
        _windowPeak = 0;
        _lastWindowMs = 0;
        LastLevelDb = -96;
        PeakDb = -96;
    }
}
