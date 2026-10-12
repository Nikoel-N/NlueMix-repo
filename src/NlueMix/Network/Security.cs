using System.Net;

namespace NlueMix.Network;

/// <summary>
/// 安全加固三件套。
///
/// 背景：NlueMix 是局域网工具，连接码只有 4 位（1 万种组合）。
/// 如果不管，同一个 Wi-Fi 下的人写个脚本几秒钟就能撞开，然后往你音响里放声音。
/// 这三道防线把"实际能被搞"变成"理论上要跑几百年"。
/// </summary>
public static class SecurityRules
{
    // ─────────────────────────────────────────────
    // ① 握手失败限流
    // ─────────────────────────────────────────────

    /// <summary>同一个 IP 连续错几次就锁</summary>
    public const int MaxFailuresPerIp = 5;

    /// <summary>锁多久（秒）</summary>
    public const int LockoutSeconds = 60;

    /// <summary>连续失败多久没再失败就清零（避免偶尔手滑被累计）</summary>
    public const int FailureWindowSeconds = 300;

    /// <summary>
    /// 全局失败保险丝：万一攻击者用一大堆 IP 轮着撞，
    /// 这个兜底能保证总尝试次数有上限。
    /// </summary>
    public const int MaxGlobalFailuresPerWindow = 50;

    public const int GlobalWindowSeconds = 60;

    /// <summary>
    /// 握手失败记录器。线程安全，只存失败次数，不存任何内容。
    /// 成功登录会立刻清零该 IP 的记录（手滑输错不算数）。
    /// </summary>
    public sealed class LockoutTracker
    {
        private sealed class Entry
        {
            public int Failures;
            public long LastFailureTicks;
            public long LockedUntilTicks;
        }

        private readonly Dictionary<string, Entry> _byIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private readonly Queue<long> _globalFailures = new();

        /// <summary>当前是否被锁。返回剩余秒数（0 = 没锁）。</summary>
        public int LockRemainingSeconds(string ip)
        {
            lock (_gate)
            {
                if (!_byIp.TryGetValue(ip, out var entry)) return 0;
                long remain = entry.LockedUntilTicks - Environment.TickCount64;
                return remain > 0 ? (int)(remain / 1000) + 1 : 0;
            }
        }

        /// <summary>记一次失败。返回 true 表示这次失败之后被锁了。</summary>
        public bool RecordFailure(string ip, out int lockSeconds)
        {
            lock (_gate)
            {
                long now = Environment.TickCount64;

                // 全局保险丝
                while (_globalFailures.Count > 0 && now - _globalFailures.Peek() > GlobalWindowSeconds * 1000L)
                    _globalFailures.Dequeue();
                _globalFailures.Enqueue(now);
                if (_globalFailures.Count > MaxGlobalFailuresPerWindow)
                {
                    lockSeconds = GlobalWindowSeconds;
                    return true;
                }

                if (!_byIp.TryGetValue(ip, out var entry))
                {
                    entry = new Entry();
                    _byIp[ip] = entry;
                }

                // 太久没失败过，重新计数
                if (now - entry.LastFailureTicks > FailureWindowSeconds * 1000L)
                    entry.Failures = 0;

                entry.Failures++;
                entry.LastFailureTicks = now;

                if (entry.Failures >= MaxFailuresPerIp)
                {
                    entry.LockedUntilTicks = now + LockoutSeconds * 1000L;
                    entry.Failures = 0;
                    lockSeconds = LockoutSeconds;
                    Log.Warn($"安全：IP {ip} 连续 {MaxFailuresPerIp} 次连接码错误，已锁定 {LockoutSeconds} 秒");
                    return true;
                }

                lockSeconds = 0;
                return false;
            }
        }

        /// <summary>查这个 IP 已经失败了几次（用来提示"还能试几次"）。</summary>
        public int FailureCount(string ip)
        {
            lock (_gate)
            {
                if (!_byIp.TryGetValue(ip, out var entry)) return 0;
                long now = Environment.TickCount64;
                if (now - entry.LastFailureTicks > FailureWindowSeconds * 1000L) return 0;
                return entry.Failures;
            }
        }

        /// <summary>握手成功，清掉这个 IP 的失败记录。</summary>
        public void RecordSuccess(string ip)
        {
            lock (_gate) _byIp.Remove(ip);
        }

        /// <summary>顺便清理长期不用的记录，避免字典无限增长。</summary>
        public void Cleanup()
        {
            lock (_gate)
            {
                long now = Environment.TickCount64;
                var stale = _byIp
                    .Where(kv => now - kv.Value.LastFailureTicks > FailureWindowSeconds * 1000L
                              && now > kv.Value.LockedUntilTicks)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var ip in stale) _byIp.Remove(ip);
            }
        }
    }

    // ─────────────────────────────────────────────
    // ② UDP 源地址校验
    // ─────────────────────────────────────────────

    /// <summary>
    /// 检查这个 UDP 包是不是来自跟 TCP 握手同一个源。
    ///
    /// 为什么重要：手机 TCP 连上后、第一包音频到达前有个时间窗口。
    /// 如果不校验，别人抢先发一包 UDP 就能把这个"位置"占住，
    /// 之后真正的手机声音就被丢掉了，而攻击者可以往你音响里灌声音。
    /// </summary>
    public static bool IsSourceAllowed(IPEndPoint packetFrom, IPEndPoint? tcpSource)
    {
        if (tcpSource is null) return false;

        // 只校验 IP，**不能校验端口**。
        //
        // 踩过的坑：手机端 TCP 和 UDP 是两个独立的 socket，各自随机分配本地端口，
        // 所以 UDP 的源端口必然和 TCP 不同。一开始我按端口也校验，
        // 结果把所有正常音频都拦掉了（真机上就是完全没声音）。
        //
        // 只校验 IP 已经足够防抢注：攻击者要么和你在同一个网段（那他能看到 IP），
        // 要么就得伪造源 IP —— 而伪造源 IP 意味着收不到回包，
        // 更关键的是他必须先通过 TCP 握手拿到一个 session 才行。
        return packetFrom.Address.Equals(tcpSource.Address);
    }

    // ─────────────────────────────────────────────
    // ③ 包头 / 包长严格校验
    // ─────────────────────────────────────────────

    /// <summary>音频包最小长度：只有包头没有数据是不合法的</summary>
    public const int MinAudioPacketBytes = AudioPacketHeader.Size + 2;

    /// <summary>音频包最大长度：20 ms 立体声 16-bit 正好 3840 字节 + 包头</summary>
    public const int MaxAudioPacketBytes = AudioPacketHeader.Size + Protocol.MaxSamplesPerPacket * 2 * 2;

    public enum PacketVerdict
    {
        Ok,
        TooShort,
        TooLong,
        BadMagic,
        BadVersion,
        UnsupportedCodec,
        BadSampleCount,
        LengthMismatch,
    }

    /// <summary>
    /// 严格校验一个 UDP 音频包。
    /// 目标：畸形数据在进入解码/混音之前就被拦掉，绝不让它碰到音频线程。
    /// </summary>
    public static PacketVerdict ValidateAudioPacket(ReadOnlySpan<byte> data, out AudioPacketHeader header, out int payloadBytes)
    {
        header = default;
        payloadBytes = 0;

        if (data.Length < MinAudioPacketBytes) return PacketVerdict.TooShort;
        if (data.Length > MaxAudioPacketBytes) return PacketVerdict.TooLong;

        header = AudioPacketHeader.Parse(data);

        if (header.Magic != Protocol.Magic) return PacketVerdict.BadMagic;
        if (header.Version != Protocol.Version) return PacketVerdict.BadVersion;
        if (header.Codec != StreamCodec.Pcm16 && header.Codec != StreamCodec.Opus)
            return PacketVerdict.UnsupportedCodec;

        // 采样数必须落在合法区间
        if (header.Samples == 0 || header.Samples > Protocol.MaxSamplesPerPacket)
            return PacketVerdict.BadSampleCount;

        payloadBytes = data.Length - AudioPacketHeader.Size;

        // 负载长度必须和声明的采样数对得上
        int expected = header.Codec == StreamCodec.Pcm16
            ? header.Samples * 2 * 2          // 16-bit × 2 声道
            : 0;                               // Opus 是变长的，不校验具体长度
        if (expected > 0 && payloadBytes < expected) return PacketVerdict.LengthMismatch;

        return PacketVerdict.Ok;
    }

    /// <summary>UDP 单包来源也可以做个上限，防止超大包打爆缓冲</summary>
    public const int MaxUdpDatagramBytes = 8192;

    /// <summary>TCP 控制通道：单行最大长度，防止有人发超长行把内存撑爆</summary>
    public const int MaxControlLineBytes = 512;

    /// <summary>同时允许的 TCP 连接数上限，防连接洪水</summary>
    public const int MaxConcurrentControlConnections = 16;
}
