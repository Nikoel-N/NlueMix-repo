using NlueMix.Core;
using NAudio.Wave;

namespace NlueMix.Audio;

/// <summary>
/// 从 AudioRing 取数据的 ISampleProvider。
/// 计数器用来统计"到底有没有真的欠载"，而不是靠缓冲区瞬间水位误判。
/// </summary>
public sealed class RingSampleProvider : ISampleProvider
{
    private readonly AudioRing _ring;
    private readonly int _channels;
    private readonly float[] _scratch;
    private readonly int _chunkFrames;
    private long _lastUnderflow;
    private long _lastOverflow;

    public RingSampleProvider(AudioRing ring, int sampleRate, int channels, int chunkFrames = 480)
    {
        _ring = ring;
        _channels = channels;
        SampleRate = sampleRate;
        _chunkFrames = chunkFrames;
        _scratch = new float[chunkFrames * channels];
    }

    public int SampleRate { get; }
    public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, _channels);
    public int AvailableFrames => _ring.Available;
    public int BufferLatencyMs => (int)(_ring.Available * 1000L / SampleRate);
    public long NewUnderruns => _ring.UnderflowFrames - _lastUnderflow;
    public long NewOverflows => _ring.OverflowFrames - _lastOverflow;

    public void SnapshotCounters()
    {
        _lastUnderflow = _ring.UnderflowFrames;
        _lastOverflow = _ring.OverflowFrames;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int produced = 0;
        while (produced < count)
        {
            int frames = Math.Min(_chunkFrames, (count - produced) / _channels);
            if (frames <= 0) break;
            int samples = frames * _channels;

            Span<float> dst = buffer.AsSpan(offset + produced, samples);
            _ring.Read(dst);

            produced += samples;
        }
        return produced;
    }
}
