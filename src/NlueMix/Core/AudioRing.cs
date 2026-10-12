namespace NlueMix.Core;

/// <summary>
/// 极简浮点环形缓冲。生产者/消费者各自一个线程，用 lock 保护，
/// 临界区只有一次数组拷贝，实测对音频线程没有可感知影响，且比无锁实现更容易保证正确。
/// </summary>
public sealed class AudioRing
{
    private readonly float[] _buffer;
    private readonly object _gate = new();
    private int _read, _write, _count;
    private float _lastSample;
    private long _overflowFrames, _underflowFrames, _nonFiniteSamples;
    private long _lastNonFiniteReport;

    public AudioRing(int capacityFrames, int channels)
    {
        Channels = channels;
        _buffer = new float[Math.Max(capacityFrames, 64) * channels];
    }

    public int Channels { get; }
    public int CapacityFrames => _buffer.Length / Channels;
    public long OverflowFrames => Interlocked.Read(ref _overflowFrames);
    public long UnderflowFrames => Interlocked.Read(ref _underflowFrames);

    /// <summary>
    /// 被消毒掉的非法浮点数（NaN / ±Inf）个数。
    ///
    /// 为什么要单独计数：这类值一旦进入音频链就【再也出不去】——
    ///   1. 环的 _lastSample 会把它记下来，欠载时反复重新注入；
    ///   2. 混音器的限幅判断 "abs * gain > 1f" 对 NaN 恒为 false，等于原样放行；
    ///   3. 结果就是持续的巨响（"炸麦"）、且不会自行恢复。
    /// 之前完全不可见，所以只能靠猜。现在它会出现在诊断日志里。
    /// </summary>
    public long NonFiniteSamples => Interlocked.Read(ref _nonFiniteSamples);

    public int Available
    {
        get { lock (_gate) return _count / Channels; }
    }

    public void Write(ReadOnlySpan<float> data)
    {
        if (data.Length == 0) return;
        int frames = data.Length / Channels;
        if (frames > CapacityFrames) data = data.Slice((frames - CapacityFrames) * Channels);
        frames = data.Length / Channels;

        lock (_gate)
        {
            if (_count / Channels + frames > CapacityFrames)
            {
                // 满了就丢最旧的，保证低延迟优先于完整性
                int drop = _count / Channels + frames - CapacityFrames;
                _read = (_read + drop * Channels) % _buffer.Length;
                _count -= drop * Channels;
                Interlocked.Add(ref _overflowFrames, drop);
            }
            int bad = 0;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                // ★ 消毒：NaN / ±Inf 绝不能进环。
                //   它们会被 _lastSample 记住并在每次欠载时重新注入，
                //   于是"响一声就坏、且永远坏下去"。
                if (!float.IsFinite(v)) { v = 0f; bad++; }
                _buffer[_write] = v;
                _write = (_write + 1) % _buffer.Length;
            }
            _count += data.Length;
            // 注意：_lastSample 取的是【已消毒】之后的值
            if (data.Length > 0) _lastSample = _buffer[(_write - 1 + _buffer.Length) % _buffer.Length];
            if (bad > 0) ReportNonFinite(bad, "写入");
        }
    }

    public int Read(Span<float> destination)
    {
        int want = destination.Length;
        lock (_gate)
        {
            int give = Math.Min(want, _count);
            for (int i = 0; i < give; i++)
            {
                destination[i] = _buffer[_read];
                _read = (_read + 1) % _buffer.Length;
            }
            _count -= give;
            if (give > 0) _lastSample = destination[give - 1];
            if (give < want)
            {
                // 欠载：用最后一个样本的衰减值填，听感上是"静音"而不是"咔"的一声
                // ★ _lastSample 若为 NaN，下面的 MathF.Abs(v) < 1e-4f 恒为 false，
                //   NaN 就会穿过这道"静音"检查铺满整个缓冲区。所以先消毒。
                float last = float.IsFinite(_lastSample) ? _lastSample : 0f;
                float v = last * 0.5f;
                if (MathF.Abs(v) < 1e-4f) v = 0;
                for (int i = give; i < want; i++) destination[i] = v;
                Interlocked.Add(ref _underflowFrames, (want - give) / Channels);
            }
        }
        return want;
    }

    public void Clear()
    {
        lock (_gate) { _read = _write = _count = 0; _lastSample = 0; }
    }

    /// <summary>记录非法样本并限频打日志（最多每 5 秒一条，避免刷屏）。</summary>
    private void ReportNonFinite(int count, string where)
    {
        long total = Interlocked.Add(ref _nonFiniteSamples, count);
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastNonFiniteReport);
        if (now - last < 5000) return;
        Interlocked.Exchange(ref _lastNonFiniteReport, now);
        Log.Warn("[音频] 检测到非法浮点样本（NaN/Inf）累计 " + total + " 个，位置：" + where
                 + "。已按静音处理；如果频繁出现说明上游有数值问题。");
    }
}
