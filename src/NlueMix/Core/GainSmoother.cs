namespace NlueMix.Core;

/// <summary>
/// 增益平滑器：所有音量变化都走这里，保证是渐变而不是"咔"的一声。
/// </summary>
public sealed class GainSmoother
{
    private readonly int _channels;
    private readonly double _sampleRate;
    private readonly double _stepPerSample;
    private float _current;
    private float _target;

    public GainSmoother(int channels, double sampleRate, int rampMs = 90, float initial = 0f)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _stepPerSample = rampMs <= 0 ? 1.0 : 1.0 / (rampMs / 1000.0 * sampleRate);
        _current = _target = Math.Clamp(initial, 0f, 4f);
    }

    public float Current => _current;
    public float Target => _target;

    public void SetTarget(float value) => _target = Math.Clamp(value, 0f, 4f);

    public void SnapTo(float value) => _current = _target = Math.Clamp(value, 0f, 4f);

    /// <summary>就地作用到一段交错样本上。</summary>
    public void Apply(Span<float> samples)
    {
        if (Math.Abs(_current - _target) < 1e-5f)
        {
            float g = _target;
            if (g == 1f) return;
            for (int i = 0; i < samples.Length; i++) samples[i] *= g;
            return;
        }

        for (int i = 0; i < samples.Length; i += _channels)
        {
            if (_current < _target)
                _current = (float)Math.Min(_target, _current + _stepPerSample);
            else
                _current = (float)Math.Max(_target, _current - _stepPerSample);

            for (int ch = 0; ch < _channels && i + ch < samples.Length; ch++)
                samples[i + ch] *= _current;
        }
    }
}
