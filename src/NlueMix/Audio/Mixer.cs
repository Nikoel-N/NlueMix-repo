using NlueMix.Core;
using NAudio.Wave;

namespace NlueMix.Audio;

/// <summary>
/// 混音器。
///
/// 【重要设计变更 —— 修回音】
/// 老版本把"电脑自己的声音"环回录进来又放回去，和原本那一份重叠，听起来就是多重回音。
///
/// 现在改成：电脑声音照常由系统播放，NlueMix 只负责把手机声音混进去。
/// 这样电脑音频通路完全没被动过，不可能产生回音。
///
/// "让路"逻辑照旧：手机一出声，就把其他程序的音量压低/静音（按音频会话控制）。
/// </summary>
public sealed class Mixer : ISampleProvider, IDisposable
{
    private readonly AudioRing[] _phoneRings;
    private readonly GainSmoother[] _phoneGains;
    private readonly SilenceDetector[] _phoneDetectors;
    private readonly GainSmoother _masterGain;
    private readonly float[] _phoneBuffer;
    private readonly float[] _scratchBuffer;
    private readonly float[] _mixBuffer;
    private readonly System.Threading.Timer _tickTimer;

    private volatile bool _phoneLoud;
    private volatile bool _manualYield;
    private volatile float _duckMultiplier = 1f;
    private float _limiterGain = 1f;

    public Mixer(AudioRing[] phoneRings, int sampleRate, int chunkFrames)
    {
        _phoneRings = phoneRings;
        SampleRate = sampleRate;
        ChunkFrames = chunkFrames;

        _phoneGains = new GainSmoother[phoneRings.Length];
        _phoneDetectors = new SilenceDetector[phoneRings.Length];
        // ★ 别漏了这个：诊断数组也必须分配。
        //   漏了的话 RenderChunk 每次都会 NullReferenceException，
        //   混音器一个样本都输出不了 —— 表现是"直接没声音"，
        //   而日志里是刷屏的 FATAL。这个错犯过一次，写在这里提醒。
        _lastAvailable = new int[phoneRings.Length];
        for (int i = 0; i < phoneRings.Length; i++)
        {
            _phoneGains[i] = new GainSmoother(2, sampleRate, 140, 0f);
            _phoneDetectors[i] = new SilenceDetector();
        }

        _masterGain = new GainSmoother(2, sampleRate, 90, 0.85f);
        _phoneBuffer = new float[chunkFrames * 2];
        _scratchBuffer = new float[chunkFrames * 2];
        _mixBuffer = new float[chunkFrames * 2];

        _tickTimer = new System.Threading.Timer(_ => EvaluateDuck(), null, 200, 200);
    }

    public int SampleRate { get; }
    public int ChunkFrames { get; }
    public AudioRing[] PhoneRings => _phoneRings;
    public SilenceDetector[] PhoneDetectors => _phoneDetectors;

    /// <summary>手机是否正在出声</summary>
    public bool PhoneLoud => _phoneLoud;

    /// <summary>是否正在让路</summary>
    public bool IsYielding { get; private set; }

    public DuckMode Mode { get; set; } = DuckMode.AutoSilence;
    public float DuckLevel { get; set; } = 0.2f;

    /// <summary>电脑声音当前应该被压到多少（1 = 不压，0 = 全静音）</summary>
    public float DuckMultiplier => _duckMultiplier;

    public bool ManualYield
    {
        get => _manualYield;
        set { _manualYield = value; EvaluateDuck(); }
    }

    public event EventHandler<bool>? YieldingChanged;

    public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    public void SetMasterVolume(float volume) => _masterGain.SetTarget(Math.Clamp(volume, 0f, 1.5f));

    public void SetPhoneVolume(int index, float volume)
    {
        if (index >= 0 && index < _phoneGains.Length) _phoneGains[index].SetTarget(Math.Clamp(volume, 0f, 1.5f));
    }

    public float GetPhoneGain(int index)
        => index >= 0 && index < _phoneGains.Length ? _phoneGains[index].Current : 0f;

    /// <summary>注入某一路手机的静音检测器（必须和 PhoneSession 用同一个实例）</summary>
    public void SetPhoneDetector(int index, SilenceDetector detector)
    {
        if (index < 0 || index >= _phoneDetectors.Length) return;
        _phoneDetectors[index] = detector;
    }

    private long _readCalls;

    public int Read(float[] buffer, int offset, int count)
    {
        Interlocked.Increment(ref _readCalls);

        try
        {
            int produced = 0;
            while (produced < count)
            {
                int frames = Math.Min(ChunkFrames, (count - produced) / 2);
                if (frames <= 0) break;

                RenderChunk(frames);

                int samples = frames * 2;
                // NAudio 传进来的可能是 WaveBuffer（联合体），Array.Copy 会抛类型异常，必须用 BlockCopy
                Buffer.BlockCopy(_mixBuffer, 0, buffer, (offset + produced) * 4, samples * 4);
                produced += samples;
            }
            return produced;
        }
        catch (Exception ex)
        {
            Log.Fatal("混音渲染异常", ex);
            Array.Clear(buffer, offset, count);
            return count;
        }
    }

    /// <summary>只混手机声音（电脑声音由系统自己播，不经过这里，所以不会有回音）</summary>
    private void RenderChunk(int frames)
    {
        int samples = frames * 2;

        _phoneBuffer.AsSpan(0, samples).Clear();
        for (int i = 0; i < _phoneRings.Length; i++)
        {
            // 记录每个槽位在读之前有多少帧可用 —— 这是判断"到底哪一路在供数据"的唯一可靠依据。
            // 之前只知道"总共欠载了多少"，不知道是哪一路空的，只能靠推理。
            _lastAvailable[i] = _phoneRings[i].Available;

            _phoneRings[i].Read(_scratchBuffer.AsSpan(0, samples));
            _phoneGains[i].Apply(_scratchBuffer.AsSpan(0, samples));
            for (int s = 0; s < samples; s++) _phoneBuffer[s] += _scratchBuffer[s];
        }

        // 软限幅：多路手机叠加可能超过 1.0
        float releasePerFrame = 1f / (0.4f * SampleRate);
        for (int s = 0; s < samples; s++)
        {
            float v = _phoneBuffer[s];

            // ★ 消毒：NaN / ±Inf 到这里必须清掉。
            //   限幅判断 "abs * _limiterGain > 1f" 对 NaN 恒为 false，
            //   等于把 NaN 原样送到 WASAPI —— 听感就是持续巨响（"炸麦"）。
            if (!float.IsFinite(v))
            {
                _mixBuffer[s] = 0f;
                _nonFinitePhoneSamples++;
                if ((_nonFinitePhoneSamples & 0x3FF) == 1) _limiterGain = 1f;   // 顺便让限幅器复位
                continue;
            }

            float abs = MathF.Abs(v);
            if (abs * _limiterGain > 1f)
                _limiterGain = Math.Max(0.05f, Math.Min(_limiterGain, 0.98f / abs));
            else
                _limiterGain = MathF.Min(1f, _limiterGain + releasePerFrame);

            _mixBuffer[s] = v * _limiterGain;
        }

        // 记录这一块混音输出的峰值 —— 用来回答"引擎此刻到底在输出什么"。
        // 关键用途：如果所有采集源都断了、各环都为 0，输出峰值却仍然很高，
        // 那就说明噪声不是采集来的，而是引擎自己产生的（反之则噪声来自别处）。
        float peak = 0f;
        for (int s = 0; s < samples; s++)
        {
            float a = MathF.Abs(_mixBuffer[s]);
            if (a > peak) peak = a;
        }
        _lastOutputPeak = peak;

        _masterGain.Apply(_mixBuffer.AsSpan(0, samples));
        // ★ 修复：混音结果已经由上面的 Read() 直接拷进调用方(WASAPI)的缓冲区了，
        //   这里绝对不能再往一个"没人读"的环形缓冲写第二份 ——
        //   那会让环永远处于满溢状态，BufferLatencyMs 永远显示 500 ms、
        //   DroppedFrames 一路虚增。（旧代码就是这么错的）
    }

    /// <summary>被消毒掉的手机侧非法样本数（诊断用，见 AudioRing.NonFiniteSamples 的说明）。</summary>
    private long _nonFinitePhoneSamples;

    /// <summary>每个槽位在最近一次混音时"可读帧数"，用来定位是哪一路在（不）供数据。</summary>
    private readonly int[] _lastAvailable;

    /// <summary>最近一次混音的峰值（0..1+）。</summary>
    private float _lastOutputPeak;

    /// <summary>暴露给界面/日志，便于下次出问题时直接看数字而不是猜。</summary>
    public long NonFinitePhoneSamples => Interlocked.Read(ref _nonFinitePhoneSamples);

    // ★ 音频健康诊断：每 5 秒一行，把"只能靠猜"的东西变成数字。
    //
    //   之前排查"放一会就炸麦、一点原声都没有"时，手上只有听感，
    //   没有环溢出/欠载/非法样本的任何计数，只能一个个假设去试，非常低效。
    //   这一行就是为了以后不用再猜：出问题时直接看这几个数字。
    private int _healthTick;

    private void ReportHealth()
    {
        try
        {
            if (++_healthTick % 25 != 0) return;   // 200ms × 25 = 5 秒

            long overflow = 0, underflow = 0, nonFinite = 0;
            for (int i = 0; i < _phoneRings.Length; i++)
            {
                overflow += _phoneRings[i].OverflowFrames;
                underflow += _phoneRings[i].UnderflowFrames;
                nonFinite += _phoneRings[i].NonFiniteSamples;
            }
            nonFinite += _nonFinitePhoneSamples;

            // 每槽位可读帧数：一眼看出哪一路在供数据、哪一路是空的
            var avail = new System.Text.StringBuilder();
            for (int i = 0; i < _lastAvailable.Length; i++)
            {
                if (i > 0) avail.Append('/');
                avail.Append(_lastAvailable[i]);
            }

            bool quiet = overflow == 0 && underflow == 0 && nonFinite == 0;
            bool outputting = _lastOutputPeak > 1e-4f;

            // 正常运行时（无异常且没在出声）保持日志干净；
            // 但只要"没数据却在出声"或"有异常"，就必须留下证据。
            if (quiet && !outputting) return;

            Log.Warn("[音频健康] 输出峰值=" + _lastOutputPeak.ToString("0.####")
                     + " 各槽可读帧=[" + avail + "]"
                     + " 环溢出=" + overflow + " 环欠载=" + underflow
                     + " 非法样本=" + nonFinite
                     + " 限幅增益=" + _limiterGain.ToString("0.###")
                     + " 总增益=" + _masterGain.Current.ToString("0.###"));
        }
        catch { }
    }

    /// <summary>每 200 ms 判定一次该不该给手机让路</summary>
    private void EvaluateDuck()
    {
        try
        {
            ReportHealth();
            bool loud = false;
            for (int i = 0; i < _phoneDetectors.Length; i++)
            {
                if (_phoneDetectors[i].IsLoud) { loud = true; break; }
            }
            _phoneLoud = loud;

            bool shouldYield = Mode switch
            {
                DuckMode.AutoSilence => loud,
                DuckMode.AutoDuck => loud,
                DuckMode.Mix => false,
                DuckMode.Manual => _manualYield,
                _ => loud,
            };

            _duckMultiplier = shouldYield
                ? (Mode == DuckMode.AutoDuck ? Math.Clamp(DuckLevel, 0f, 1f) : 0f)
                : 1f;

            if (IsYielding != shouldYield)
            {
                IsYielding = shouldYield;
                try { YieldingChanged?.Invoke(this, shouldYield); } catch { }
                Log.Info(shouldYield ? "★ 手机开始播放 → 电脑声音让路" : "★ 手机已安静 → 电脑声音恢复");
            }
        }
        catch (Exception ex)
        {
            Log.Error("让路判定异常: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _tickTimer.Dispose();
    }
}
