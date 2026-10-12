namespace NlueMix.Network;

/// <summary>
/// NlueMix 局域网串流协议 v1。
///
/// 设计目标：简单、能调试、丢了几个包也不会爆音。
///  - 控制通道 TCP：连接、握手、心跳、断线感知
///  - 数据通道 UDP：音频帧，带序号，丢了就丢，不重传（重传只会让声音更糟）
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const int ControlPort = 47820;
    public const int AudioPort = 47821;

    /// <summary>单包最多装多少采样（20 ms @ 48 kHz 立体声 = 1920）。留点余量。</summary>
    public const int MaxSamplesPerPacket = 4096;

    /// <summary>PCM 包：头(16) + 采样数*2*2 字节</summary>
    public const int MaxPcmPacketBytes = 16 + MaxSamplesPerPacket * 4;

    public const uint Magic = 0x4D42_4C55; // "ULBM" 小端读作 'BLUM'
}

/// <summary>数据包编码方式。</summary>
public enum StreamCodec : byte
{
    /// <summary>16-bit 小端 PCM。局域网首选，零损失。</summary>
    Pcm16 = 0,
    /// <summary>Opus 压缩。Wi-Fi 差的时候用。</summary>
    Opus = 1,
}

/// <summary>UDP 音频包头，固定 16 字节，小端。</summary>
public readonly struct AudioPacketHeader
{
    public const int Size = 16;

    public uint Magic { get; init; }
    public byte Version { get; init; }
    public StreamCodec Codec { get; init; }
    public byte Flags { get; init; }
    public uint Sequence { get; init; }
    public ushort Samples { get; init; }
    public ushort Reserved { get; init; }
    public uint TimestampMs { get; init; }

    public static AudioPacketHeader Parse(ReadOnlySpan<byte> data)
    {
        return new AudioPacketHeader
        {
            Magic = BitConverter.ToUInt32(data[..4]),
            Version = data[4],
            Codec = (StreamCodec)data[5],
            Flags = data[6],
            Sequence = BitConverter.ToUInt32(data.Slice(8, 4)),
            Samples = BitConverter.ToUInt16(data.Slice(12, 2)),
            Reserved = BitConverter.ToUInt16(data.Slice(14, 2)),
            TimestampMs = 0,
        };
    }

    public static void Write(Span<byte> destination, StreamCodec codec, uint sequence, ushort samples)
    {
        BitConverter.TryWriteBytes(destination[..4], Protocol.Magic);
        destination[4] = Protocol.Version;
        destination[5] = (byte)codec;
        destination[6] = 0;
        destination[7] = 0;
        BitConverter.TryWriteBytes(destination.Slice(8, 4), sequence);
        BitConverter.TryWriteBytes(destination.Slice(12, 2), samples);
        BitConverter.TryWriteBytes(destination.Slice(14, 2), (ushort)0);
    }
}

/// <summary>控制通道消息（一行一条，制表符分隔）。</summary>
public static class ControlMessages
{
    public const string Hello = "HELLO";
    public const string Welcome = "WELCOME";
    public const string Reject = "REJECT";
    public const string Ping = "PING";
    public const string Pong = "PONG";
    public const string Bye = "BYE";
    public const string Level = "LEVEL";

    /// <summary>
    /// 手机上报的设备侧诊断（谁在播放、AudioAttributes.flags 是多少）。
    /// 走控制通道，因此不需要 adb 也能拿到设备真值。
    /// </summary>
    public const string Diag = "DIAG";

    /// <summary>
    /// 手机端自动配置读到的无线调试信息。
    ///
    /// 格式：AUTOCFG 	 ip 	 连接端口 	 配对端口 	 配对码
    /// 后两项可以为空（已经配对过就不需要）。
    ///
    /// 走控制通道的原因和 DIAG 一样：这条通道 App 路径就有，
    /// 不需要先有 adb —— 正好用它来把 adb 需要的端口送过来，
    /// 形成一个"零调试先连上、再自动升级到全功能"的闭环。
    /// </summary>
    public const string AutoCfg = "AUTOCFG";

    /// <summary>拼 AUTOCFG 消息。配对端口/配对码可以为空。</summary>
    public static string FormatAutoCfg(string ip, int connectPort, int? pairPort, string? pairCode)
        => string.Join('\t', AutoCfg, Sanitize(ip), connectPort,
            pairPort?.ToString() ?? "", Sanitize(pairCode ?? ""));

    /// <summary>HELLO  v  deviceId  deviceName  code  sampleRate  channels  codec</summary>
    public static string FormatHello(int version, string deviceId, string deviceName, string code, int sampleRate, int channels, StreamCodec codec)
        => string.Join('\t', Hello, version, Sanitize(deviceId), Sanitize(deviceName), code, sampleRate, channels, (int)codec);

    public static string FormatWelcome(int slot, string pcName, int sampleRate, int volumePercent)
        => string.Join('\t', Welcome, slot, Sanitize(pcName), sampleRate, volumePercent);

    public static string FormatReject(string reason) => string.Join('\t', Reject, Sanitize(reason));

    private static string Sanitize(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
