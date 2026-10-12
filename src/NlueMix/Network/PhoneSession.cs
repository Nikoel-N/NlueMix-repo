using System.Net;
using System.Net.Sockets;
using System.Text;
using Concentus;
using NlueMix.Audio;
using NlueMix.Core;

namespace NlueMix.Network;

/// <summary>
/// 一台手机在电脑这边的会话。
/// 生命周期：TCP 握手成功 → Online；UDP 断流 6 秒 → 标记离线但保留位置，等它回来。
/// </summary>
public sealed class PhoneSession : IDisposable
{
    private readonly AudioRing _ring;
    private readonly Action<PhoneSession> _onChanged;

    private IOpusDecoder? _decoder;
    private int _decoderRate;
    private int _decoderChannels;
    private float[] _decodeBuffer = new float[Protocol.MaxSamplesPerPacket * 2];
    private float[] _resampleCarryBuffer = Array.Empty<float>();
    private double _resampleCarry;
    private uint _lastSequence;
    private bool _hasSequence;
    private int _gapLogs;
    private long _packets, _lost;
    private long _lastPacketTicks;

    public PhoneSession(int slot, AudioRing ring, Action<PhoneSession> onChanged)
    {
        Slot = slot;
        _ring = ring;
        _onChanged = onChanged;
    }

    public int Slot { get; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "手机";
    public IPEndPoint? AudioEndPoint { get; set; }

    /// <summary>TCP 握手的来源地址。UDP 音频包必须来自这里（防抢注）。</summary>
    public IPEndPoint? TcpSource { get; set; }

    /// <summary>true = 音频走 TCP 连接（不依赖 UDP）</summary>
    public bool UseTcpAudio { get; set; }
    public TcpClient? Tcp { get; set; }
    public StreamCodec Codec { get; set; } = StreamCodec.Pcm16;
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public bool Online { get; private set; }
    public long Packets => Interlocked.Read(ref _packets);
    public long LostPackets => Interlocked.Read(ref _lost);
    public double LevelDb { get; private set; } = -96;
    public double PeakDb { get; private set; } = -96;
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public string CodecName => Codec == StreamCodec.Pcm16 ? "PCM 无损" : "Opus 256k";

    public SilenceDetector Detector { get; } = new();

    public bool IsStale => Environment.TickCount64 - Interlocked.Read(ref _lastPacketTicks) > 6000;

    public void MarkOnline()
    {
        if (Online) return;
        Online = true;
        Detector.Reset();
        _onChanged(this);
    }

    /// <summary>喂一包音频数据（已经剥掉包头）。</summary>
    public void Feed(ReadOnlySpan<byte> payload, ushort sampleCount)
    {
        Interlocked.Exchange(ref _lastPacketTicks, Environment.TickCount64);
        Interlocked.Increment(ref _packets);

        try
        {
            float[] samples;
            int produced;

            if (Codec == StreamCodec.Opus)
            {
                EnsureDecoder();
                if (_decoder is null) return;
                int wanted = sampleCount > 0 ? sampleCount : 960;
                if (_decodeBuffer.Length < wanted * Channels) _decodeBuffer = new float[wanted * Channels];
                produced = OpusCodec.Decode(_decoder, payload, wanted, Channels, _decodeBuffer);
                samples = _decodeBuffer;
            }
            else
            {
                // ★ 修复：只取包头声明的那点采样，多出来的尾字节不能当音频解出来
                //   （否则每包结尾都会多出一小段刺耳的噪声，长度不是 4 的倍数时还会错位）
                int want = sampleCount > 0
                    ? Math.Min(sampleCount * Channels * 2, payload.Length)
                    : payload.Length;
                samples = Dsp.Pcm16ToFloat(payload.Slice(0, want));
                produced = samples.Length;
            }

            if (produced <= 0) return;

            Span<float> span = samples.AsSpan(0, produced);

            // 手机采样率和引擎不一致时重采样（大部分手机就是 48 kHz，这条通常不走）
            if (SampleRate != AudioEngine.SampleRate)
            {
                double ratio = (double)AudioEngine.SampleRate / SampleRate;
                var resampled = Dsp.ResampleLinear(samples.AsSpan(0, produced).ToArray(), Channels, ratio, ref _resampleCarry);
                if (resampled.Length == 0) return;
                span = resampled.AsSpan();
            }

            // ★ 修复：手机声明的声道数必须转成引擎的 2 声道。
            //   单声道客户端原来会被当成 L/R 交错读，结果是变调 + 左右串音。
            if (Channels != AudioEngine.Channels)
                span = Dsp.ToChannels(span.ToArray(), Channels, AudioEngine.Channels);

            _ring.Write(span);
            Detector.Process(span, AudioEngine.SampleRate, AudioEngine.Channels);

            LevelDb = Detector.LastLevelDb;
            PeakDb = Detector.PeakDb;

            if (!Online) MarkOnline();
        }
        catch (Exception ex)
        {
            Log.Warn("处理手机音频包失败: " + ex.Message);
        }
    }

    /// <summary>根据序号统计丢包率（只用于界面显示，不做重传）。</summary>
    public void NoteSequence(uint sequence)
    {
        if (_hasSequence)
        {
            uint expected = _lastSequence + 1;
            if (sequence > expected)
            {
                uint gap = sequence - expected;
                if (gap < 500)
                {
                    Interlocked.Add(ref _lost, gap);

                    // 诊断：把跳变的真实序号打出来，定位"丢包计数虚高"的来源。
                    // 正常情况下 TCP 是字节流，序号应当严格 +1，不该出现跳变。
                    if (_gapLogs < 12 && gap > 20)
                    {
                        _gapLogs++;
                        Log.Warn("[序号诊断] " + DeviceName + " 上一包 seq=" + _lastSequence
                                 + " 本包 seq=" + sequence + " 跳变=" + gap
                                 + "（第 " + _gapLogs + " 次）");
                    }
                }
            }
        }
        _hasSequence = true;
        _lastSequence = sequence;
    }

    private void EnsureDecoder()
    {
        if (_decoder is not null && _decoderRate == SampleRate && _decoderChannels == Channels) return;
        _decoder = OpusCodec.CreateDecoder(SampleRate, Channels);
        _decoderRate = SampleRate;
        _decoderChannels = Channels;
    }

    public void MarkOffline(string reason)
    {
        if (!Online && _lastPacketTicks == 0) return;
        Online = false;
        Detector.Reset();
        LevelDb = -96;
        PeakDb = -96;
        _hasSequence = false;
        _ring.Clear();
        Log.Info("手机 " + DeviceName + " 离线: " + reason);
        _onChanged(this);
    }

    public void Send(string message)
    {
        var client = Tcp;
        if (client is null) return;
        try
        {
            if (!client.Connected) return;
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            client.GetStream().Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Log.Warn("给手机 " + DeviceName + " 发消息失败: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { Tcp?.Close(); } catch { }
        Tcp = null;
        _ring.Clear();
    }
}
