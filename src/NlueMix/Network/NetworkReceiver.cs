using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NlueMix.Audio;
using NlueMix.Core;

namespace NlueMix.Network;

/// <summary>
/// 手机接收端。两条通道：
///  - TCP 47820：握手、心跳、断线感知（可靠，但不用来传音频，避免队头阻塞）
///  - UDP 47821：音频数据（不可靠，但低延迟，丢几个包听不出来）
/// </summary>
public sealed class NetworkReceiver : IDisposable
{
    private readonly int _controlPort;
    private readonly int _audioPort;
    private readonly string _pairCode;
    private readonly AudioRing[] _rings;
    private readonly PhoneSession[] _sessions;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<PhoneSession> _onChanged;
    private readonly System.Threading.Timer _housekeeping;

    private TcpListener? _tcp;
    /// <summary>用 TCP 传音频的手机连接（当 UDP 被防火墙拦时备用）</summary>
    private UdpClient? _udp;
    private IPAddress _localAddress = IPAddress.Loopback;

    /// <summary>握手失败限流器（防暴力破解连接码）</summary>
    private readonly SecurityRules.LockoutTracker _lockout = new();

    /// <summary>当前活着的控制连接数，防连接洪水</summary>
    private int _liveConnections;

    public NetworkReceiver(
        int controlPort,
        int audioPort,
        string pairCode,
        AudioRing[] rings,
        Action<PhoneSession> onChanged)
    {
        _controlPort = controlPort;
        _audioPort = audioPort;
        _pairCode = pairCode;
        _rings = rings;
        _onChanged = onChanged;
        Slots = new PhoneSession[rings.Length];
        _sessions = Slots;
        for (int i = 0; i < rings.Length; i++) _sessions[i] = new PhoneSession(i, rings[i], onChanged);
        _housekeeping = new System.Threading.Timer(_ => Housekeeping(), null, 2000, 2000);
    }

    /// <summary>固定 3 个位置，永远不动态增减 —— 界面上的卡片位置才不会跳来跳去。</summary>
    public PhoneSession[] Slots { get; }

    public bool IsRunning { get; private set; }
    public string PairCode => _pairCode;
    public IPAddress LocalAddress => _localAddress;
    public int ControlPort => _controlPort;
    public int AudioPort => _audioPort;
    public int OnlineCount => _sessions.Count(s => s.Online);

    public string[] LocalAddresses { get; private set; } = Array.Empty<string>();

    public event EventHandler<PhoneSession>? SessionChanged;

    public void Start()
    {
        if (IsRunning) return;
        _localAddress = FindLocalAddress();
        LocalAddresses = ListLocalAddresses();

        _tcp = new TcpListener(IPAddress.Any, _controlPort);
        _tcp.Start();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _audioPort));
        _udp.Client.ReceiveBufferSize = 1 << 20;

        _ = Task.Factory.StartNew(AcceptLoop, TaskCreationOptions.LongRunning);
        _ = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);

        IsRunning = true;
        Log.Info($"网络接收已启动：控制 {_controlPort} / 音频 {_audioPort}，本机地址 {_localAddress}");
    }

    // ------------------------------------------------------------ TCP

    private async Task AcceptLoop()
    {
        var listener = _tcp;
        if (listener is null) return;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;

                // 防连接洪水：同时活着的控制连接有上限
                if (Interlocked.Increment(ref _liveConnections) > SecurityRules.MaxConcurrentControlConnections)
                {
                    Interlocked.Decrement(ref _liveConnections);
                    Log.Warn("安全：控制连接数超过上限，拒绝一个来自 "
                        + (client.Client.RemoteEndPoint as IPEndPoint)?.Address);
                    try { client.Close(); } catch { }
                    continue;
                }

                _ = Task.Factory.StartNew(() => HandleClient(client), TaskCreationOptions.LongRunning);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                Log.Warn("接受手机连接出错: " + ex.Message);
                await Task.Delay(500);
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        PhoneSession? session = null;
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        string remoteIp = remote?.Address.ToString() ?? "unknown";

        try
        {
            using (client)
            using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                // ── 防线①：这个 IP 是不是正在被锁？
                int lockRemain = _lockout.LockRemainingSeconds(remoteIp);
                if (lockRemain > 0)
                {
                    Log.Warn($"安全：拒绝来自 {remoteIp} 的连接，还在锁定期（剩 {lockRemain} 秒）");
                    WriteLine(client, ControlMessages.FormatReject($"尝试次数过多，请等 {lockRemain} 秒"));
                    return;
                }

                string? line = reader.ReadLine();

                // ── 防线③：控制行长度上限，防止有人发超长行撑爆内存
                if (line is not null && line.Length > SecurityRules.MaxControlLineBytes)
                {
                    Log.Warn("安全：来自 " + remoteIp + " 的控制行过长（" + line.Length + " 字节），已拒绝");
                    WriteLine(client, ControlMessages.FormatReject("请求不合法"));
                    return;
                }

                if (line is null) return;

                var parts = line.Split('\t');
                if (parts.Length < 4 || parts[0] != ControlMessages.Hello)
                {
                    WriteLine(client, ControlMessages.FormatReject("协议不匹配"));
                    return;
                }

                string deviceId = parts[2];
                string deviceName = parts.Length > 3 ? parts[3] : "手机";
                string code = parts.Length > 4 ? parts[4] : "";
                int sampleRate = parts.Length > 5 && int.TryParse(parts[5], out var sr) ? sr : 48000;
                int channels = parts.Length > 6 && int.TryParse(parts[6], out var ch) ? ch : 2;
                var codec = parts.Length > 7 && int.TryParse(parts[7], out var cd) && cd == (int)StreamCodec.Opus
                    ? StreamCodec.Opus
                    : StreamCodec.Pcm16;

                if (code != _pairCode)
                {
                    // 不把用户输错的内容写进日志（万一是别人的密码习惯，别记录）
                    bool nowLocked = _lockout.RecordFailure(remoteIp, out int lockSeconds);
                    if (nowLocked)
                    {
                        WriteLine(client, ControlMessages.FormatReject($"错误次数过多，已锁定 {lockSeconds} 秒"));
                    }
                    else
                    {
                        int left = SecurityRules.MaxFailuresPerIp - FailureCountFor(remoteIp);
                        Log.Warn("手机 " + deviceName + " 配对码错误（来自 " + remoteIp + "）");
                        WriteLine(client, ControlMessages.FormatReject(left > 0 ? $"配对码不对，还能试 {left} 次" : "配对码不对"));
                    }
                    return;
                }

                // 对了就清零，手滑输错几次不算数
                _lockout.RecordSuccess(remoteIp);

                session = AcquireSlot(deviceId, deviceName, sampleRate, channels, codec);
                if (session is null)
                {
                    WriteLine(client, ControlMessages.FormatReject("位置已满"));
                    return;
                }

                session.Tcp = client;
                session.TcpSource = remote;   // 防线②：UDP 包必须来自这个地址

                // 第 9 个字段是 "tcp" 时，音频直接走这条 TCP 连接（不需要 UDP）
                bool tcpAudio = parts.Length > 8 &&
                    string.Equals(parts[8], "tcp", StringComparison.OrdinalIgnoreCase);
                session.UseTcpAudio = tcpAudio;
                WriteLine(client, ControlMessages.FormatWelcome(session.Slot, Environment.MachineName, AudioEngine.SampleRate, 100));
                Log.Info($"手机已接入：{deviceName}（{session.CodecName}，{sampleRate} Hz/{channels} ch）→ 位置 {session.Slot + 1}");
                RaiseChanged(session);

                if (tcpAudio)
                {
                    Log.Info("手机 " + session.DeviceName + " 使用 TCP 传音频（真实会话）");
                    ReadTcpAudio(session, client);
                }
                else
                {
                    // UDP 模式：这条连接只负责心跳和"还活着"的判断
                    while (!_cts.IsCancellationRequested)
                    {
                        string? msg = reader.ReadLine();
                        if (msg is null) break;
                        if (msg.StartsWith(ControlMessages.Ping, StringComparison.Ordinal))
                        {
                            WriteLine(client, ControlMessages.Pong);
                        }
                        else if (msg.StartsWith(ControlMessages.Diag, StringComparison.Ordinal))
                        {
                            // 手机把设备侧诊断（正在播放的 App 及其 AudioAttributes.flags）送过来。
                            // 没有 adb 时，这就是唯一的设备真值来源。
                            var text = msg.Length > ControlMessages.Diag.Length
                                ? msg[ControlMessages.Diag.Length..].Trim()
                                : "";
                            if (text.Length > 0) Log.Info("[手机诊断] " + text);
                        }
                        else if (msg.StartsWith(ControlMessages.AutoCfg, StringComparison.Ordinal))
                        {
                            // 手机端自动配置读到的端口/配对码。在这里执行 adb，全自动。
                            HandleAutoCfg(session, msg);
                        }
                        else if (msg.StartsWith(ControlMessages.Bye, StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("手机连接异常: " + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _liveConnections);
            if (session is not null)
            {
                // ★ 只有"当前登记的那条连接"断开时才回收整个会话。
                //   否则同一台手机的旧连接超时退出时，会把新连接（真正在传音频的那条）一起关掉。
                if (session.Tcp is null || ReferenceEquals(session.Tcp, client))
                    session.Dispose();
                session.MarkOffline("连接已断开");
                RaiseChanged(session);
            }
        }
    }

    /// <summary>
    /// 从 TCP 连接里读音频帧。
    /// 格式和 UDP 完全一样：16 字节包头 + PCM 负载，只是改成流式读取。
    /// 这样即使 UDP 被防火墙拦了也能传声音。
    /// </summary>
    private void ReadTcpAudio(PhoneSession session, TcpClient client)
    {
        var stream = client.GetStream();
        // ★ 静音 ≠ 断线。
        //   手机静音时不发音频，但会每 0.5 秒发一个"保活包"（1 个静音采样，20 字节），
        //   所以这里【保留】读取超时是安全的：真掉线的手机才会连续多次超时。
        //   注意：把超时改成 0（永久阻塞）是错的 —— 手机离开 Wi-Fi 范围时属于"半开连接"，
        //   Read 会永远阻塞，会话永远回收不了，三个位置很快被僵尸占满，只能重启程序。
        stream.ReadTimeout = 8000;
        var header = new byte[AudioPacketHeader.Size];
        var payload = new byte[Protocol.MaxPcmPacketBytes];
        int consecutiveTimeouts = 0;

        while (!_cts.IsCancellationRequested && session.Tcp is not null)
        {
            try
            {
                // 读包头（必须读满 16 字节）
                if (!ReadExact(stream, header, header.Length)) break;
                consecutiveTimeouts = 0;

                var h = AudioPacketHeader.Parse(header);
                if (h.Magic != Protocol.Magic || h.Version != Protocol.Version) break;
                if (h.Samples == 0 || h.Samples > Protocol.MaxSamplesPerPacket) break;

                int bytes = h.Codec == StreamCodec.Pcm16
                    ? h.Samples * 2 * 2
                    : 4096;
                if (bytes <= 0 || bytes > payload.Length) break;

                if (!ReadExact(stream, payload, bytes)) break;

                session.NoteSequence(h.Sequence);
                session.Feed(payload.AsSpan(0, bytes), h.Samples);
            }
            catch (IOException ex) when (IsReadTimeout(ex))
            {
                // 静音期间读不到数据是正常的，给几次机会；连续超时才认定手机真的掉线。
                if (++consecutiveTimeouts >= 5)
                {
                    Log.Info("手机 " + session.DeviceName + " 连续 " + consecutiveTimeouts + " 次读超时，判定为已掉线");
                    break;
                }
                continue;
            }
            catch (IOException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("TCP 音频读取出错: " + ex.Message);
                break;
            }
        }
        Log.Info("手机 " + session.DeviceName + " 的 TCP 音频流已结束");
    }

    /// <summary>这个 IOException 是不是"读超时"（也就是暂时没数据），而不是真断开。</summary>
    private static bool IsReadTimeout(IOException ex)
        => ex.InnerException is System.Net.Sockets.SocketException se
           && (se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut
               || se.SocketErrorCode == System.Net.Sockets.SocketError.WouldBlock);

    /// <summary>从流里精确读满 n 字节，读不满返回 false。</summary>
    private static bool ReadExact(NetworkStream stream, byte[] buffer, int n)
    {
        int got = 0;
        while (got < n)
        {
            int read = stream.Read(buffer, got, n - got);
            if (read <= 0) return false;
            got += read;
        }
        return true;
    }

    private PhoneSession? AcquireSlot(string deviceId, string deviceName, int sampleRate, int channels, StreamCodec codec)
    {
        lock (_sessions)
        {
            // 同一台手机重连：复用原来的位置
            var existing = _sessions.FirstOrDefault(s => s.DeviceId == deviceId && s.Online);
            if (existing is not null)
            {
                existing.SampleRate = sampleRate;
                existing.Channels = channels;
                existing.Codec = codec;
                return existing;
            }

            // ★ 修复（兼容旧版 APK）：旧版安卓端一条手机开两条 TCP —— 控制连接用
            //   <deviceId>，音频连接用 <deviceId>-audio。旧代码把后者也当成"一台新手机"，
            //   于是一台手机吃掉两个位置（总共只有 3 个），第二台手机直接被拒"位置已满"，
            //   界面还会多出一张 "<手机名> (音频)" 的假卡片。这里让 -audio 连接挂回原会话。
            if (deviceId.EndsWith("-audio", StringComparison.OrdinalIgnoreCase))
            {
                string baseId = deviceId[..^"-audio".Length];
                var owner = _sessions.FirstOrDefault(s => s.DeviceId == baseId);
                if (owner is not null)
                {
                    owner.SampleRate = sampleRate;
                    owner.Channels = channels;
                    owner.Codec = codec;
                    return owner;
                }
            }

            var free = _sessions.FirstOrDefault(s => s.Tcp is null);
            if (free is null) return null;

            free.DeviceId = deviceId;
            free.DeviceName = deviceName;
            free.SampleRate = sampleRate;
            free.Channels = channels;
            free.Codec = codec;
            free.AudioEndPoint = null;
            free.MarkOnline();
            return free;
        }
    }

    /// <summary>
    /// 处理手机上报的自动配置信息：执行 adb pair / connect。
    ///
    /// 全程不需要用户输入 —— 端口和配对码都是手机端无障碍读出来发过来的。
    /// </summary>
    private void HandleAutoCfg(PhoneSession session, string msg)
    {
        try
        {
            var parts = msg.Split('\t');
            // parts[0] = "AUTOCFG"
            if (parts.Length < 3)
            {
                Log.Warn("[自动配置] 消息格式不对: " + msg);
                return;
            }

            var ip = parts[1].Trim();
            if (!int.TryParse(parts[2].Trim(), out var connectPort) || connectPort <= 0)
            {
                Log.Warn("[自动配置] 连接端口无效: " + msg);
                return;
            }

            int? pairPort = null;
            if (parts.Length > 3 && int.TryParse(parts[3].Trim(), out var pp) && pp > 0) pairPort = pp;

            string? pairCode = null;
            if (parts.Length > 4)
            {
                var c = parts[4].Trim();
                if (c.Length > 0) pairCode = c;
            }

            Log.Info($"[自动配置] 收到手机上报：{ip}:{connectPort}" +
                     (pairPort is null ? "（无需配对）" : $"（配对端口 {pairPort}，配对码 {pairCode}）"));

            Task.Run(() =>
            {
                var adb = new Audio.AdbCapture();
                if (adb.PairAndConnect(ip, connectPort, pairPort, pairCode, out var message))
                {
                    Log.Info("[自动配置] 成功：" + message);
                    AutoCfgSucceeded?.Invoke(ip, connectPort, message);
                }
                else
                {
                    Log.Warn("[自动配置] 失败：" + message);
                    AutoCfgFailed?.Invoke(message);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("[自动配置] 处理异常: " + ex.Message);
        }
    }

    /// <summary>自动配置成功（参数：手机 IP、连接端口、说明）。界面用来提示用户。</summary>
    public event Action<string, int, string>? AutoCfgSucceeded;

    /// <summary>自动配置失败（参数：给用户看的原因）。</summary>
    public event Action<string>? AutoCfgFailed;

    // ------------------------------------------------------------ USB（adb）采集
    //
    // 走 shell 身份 + REMOTE_SUBMIX（Android 平台的公开技术路径）：手机端 App 走 MediaProjection 采不到声音的机型
    // （实测 Redmi K70 Ultra / Android 16 / HyperOS V816 就是），
    // 改用 adb + shell 身份 + REMOTE_SUBMIX 采集。
    // 音频接进来之后，和普通手机走完全相同的槽位 / 混音 / 让路逻辑。

    private NlueMix.Audio.UsbAudioSource? _usbSource;

    public bool UsbCaptureActive => _usbSource is not null;
    public string UsbCaptureStatus { get; private set; } = "";

    /// <summary>用数据线 + adb 直接采手机声音（手机端不需要装 App、也不需要授权录屏）。</summary>
    public bool StartUsbCapture(string? phoneIp = null)
    {
        StopUsbCapture();

        var session = AcquireSlot("usb-adb", "USB 直连（adb 采集）",
            AudioEngine.SampleRate, AudioEngine.Channels, StreamCodec.Pcm16);
        if (session is null)
        {
            UsbCaptureStatus = "三个位置都满了，先断开一路手机再试。";
            Log.Warn("USB 采集：" + UsbCaptureStatus);
            return false;
        }

        var source = new NlueMix.Audio.UsbAudioSource();
        source.Frame += (buffer, count) =>
            session.Feed(buffer.AsSpan(0, count), (ushort)(count / (AudioEngine.Channels * 2)));
        source.Stopped += () =>
        {
            session.MarkOffline("USB 采集已停止");
            RaiseChanged(session);
            StopUsbCapture();
        };
        source.Failed += message =>
        {
            UsbCaptureStatus = message;
            RaiseChanged(session);
        };

        if (!source.Start(phoneIp))
        {
            UsbCaptureStatus = source.LastError ?? "手机采集启动失败";
            session.MarkOffline(UsbCaptureStatus);
            RaiseChanged(session);
            source.Dispose();
            return false;
        }

        _usbSource = source;
        UsbCaptureStatus = "正在通过 USB 采集（" + source.DeviceSerial + "）";
        Log.Info("USB 采集已接入位置 " + (session.Slot + 1));
        RaiseChanged(session);
        return true;
    }

    public void StopUsbCapture()
    {
        var source = _usbSource;
        if (source is null) return;
        _usbSource = null;
        try { source.Dispose(); } catch { }
        UsbCaptureStatus = "";
        Log.Info("USB 采集已停止");
    }

    private static void WriteLine(TcpClient client, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            client.GetStream().Write(bytes, 0, bytes.Length);
            client.GetStream().Flush();
        }
        catch { }
    }

    // ------------------------------------------------------------ UDP

    private void ReceiveLoop()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        var udp = _udp;
        if (udp is null) return;

        using var mmcss = new Audio.MmcssScope("Audio", 1); // 音频接收线程给点优待

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                byte[] data = udp.Receive(ref endpoint);

                // 超大包直接丢（防止有人用巨包打爆缓冲）
                if (data.Length > SecurityRules.MaxUdpDatagramBytes)
                {
                    // 静默丢弃 + 计入统计（被限流的日志不会变成攻击者的放大器）
                    Interlocked.Increment(ref _malformedCount);
                    continue;
                }

                // ── 防线③：包头和包长严格校验，畸形数据绝不进音频线程
                var verdict = SecurityRules.ValidateAudioPacket(data, out var header, out int payloadBytes);
                if (verdict != SecurityRules.PacketVerdict.Ok)
                {
                    LogMalformed(verdict.ToString(), endpoint);
                    continue;
                }

                // ── 防线②：来源必须是握手过的那个地址
                var session = MatchSession(endpoint);
                if (session is null) continue;

                // ★ 修复：这条会话的音频是走 TCP 的，就必须【完全忽略 UDP 包】。
                //   MatchSession 只按来源 IP 匹配 —— 而同一台手机上可能有第二个发送方
                //   （例如 NlueMix App 每 500ms 发一个 UDP 保活包），它的来源 IP 与
                //   TCP 采集端完全相同，于是会被匹配进同一个会话：
                //     · 两套序号交错 → 被误判成大量丢包（实测 35 秒虚报 9820 个）
                //     · 保活包的静音采样被混进音频 → 污染声音
                //   实测：强制停掉手机 App 后，序号跳变立刻消失。
                if (session.UseTcpAudio) continue;

                session.NoteSequence(header.Sequence);
                session.Feed(data.AsSpan(AudioPacketHeader.Size, payloadBytes), header.Samples);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (_cts.IsCancellationRequested) break;
                Log.Warn("接收音频包出错: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Warn("处理音频包出错: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 找出这个 UDP 包属于哪一路手机。
    ///
    /// 防线②在这里生效：**只认已经通过 TCP 握手、且源地址匹配的会话**。
    /// 以前是"谁先发包谁占位"，别人抢先发一包就能把位置抢走；
    /// 现在必须 IP + 端口都和握手时一致。
    /// </summary>
    private PhoneSession? MatchSession(IPEndPoint endpoint)
    {
        lock (_sessions)
        {
            // 已经绑定的：地址必须完全一致
            foreach (var session in _sessions)
            {
                if (session.AudioEndPoint is null) continue;
                if (session.AudioEndPoint.Address.Equals(endpoint.Address)
                    && session.AudioEndPoint.Port == endpoint.Port)
                    return session;
            }

            // 还没绑定的：只有"刚握手成功、还没收到过音频"的会话才能绑定，
            // 而且来源必须和它 TCP 握手时的地址一致
            foreach (var session in _sessions)
            {
                if (session.Tcp is null || session.AudioEndPoint is not null) continue;
                if (!session.Online) continue;
                if (!SecurityRules.IsSourceAllowed(endpoint, session.TcpSource))
                {
                    Log.Warn("安全：丢弃来源不匹配的音频包 " + endpoint
                        + "（该会话握手自 " + session.TcpSource + "）");
                    return null;
                }

                session.AudioEndPoint = endpoint;
                Log.Info("手机 " + session.DeviceName + " 的音频端口: " + endpoint);
                return session;
            }
        }
        return null;
    }

    /// <summary>畸形包统计：不刷屏，每 30 秒最多汇报一次。</summary>
    private long _lastMalformedLogTicks;
    private long _malformedCount;

    private void LogMalformed(string reason, IPEndPoint from)
    {
        Interlocked.Increment(ref _malformedCount);
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastMalformedLogTicks) < 30_000) return;
        Interlocked.Exchange(ref _lastMalformedLogTicks, now);
        Log.Warn($"安全：丢弃非法音频包（{reason}，来自 {from.Address}），累计 " + Interlocked.Read(ref _malformedCount) + " 个");
    }

    /// <summary>查某 IP 当前的失败次数（用来提示"还能试几次"）。</summary>
    private int FailureCountFor(string ip) => _lockout.FailureCount(ip);

    // ------------------------------------------------------------ 维护

    private void Housekeeping()
    {
        try
        {
            _lockout.Cleanup();

            foreach (var session in _sessions)
            {
                if (session.Tcp is not null && session.IsStale && session.Online)
                {
                    session.MarkOffline("超过 6 秒没有音频");
                    RaiseChanged(session);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("会话维护出错: " + ex.Message);
        }
    }

    private void RaiseChanged(PhoneSession session)
    {
        try { SessionChanged?.Invoke(this, session); } catch { }
        try { _onChanged(session); } catch { }

        // ★ 手机那路断开时，一并停掉 shell 采集。
        //
        //   为什么需要：手机 App（MediaProjection）和 shell 采集（adb + REMOTE_SUBMIX）
        //   是两条【完全独立】的通道，各占一个槽位，互不感知。
        //   实测踩到的现象：用户在手机上点「停止串流」，手机那路断了，
        //   但电脑端的 shell 采集还在跑、还在出声 —— 用户以为没停掉。
        //   用户的心智模型是「串流就是一个开关」，所以这里让它们联动。
        //
        //   为什么排除 usb-adb 自己：StopUsbCapture() 会让该槽位 MarkOffline，
        //   不排除的话会递归回头再调一次自己。
        if (!session.Online && session.DeviceId != "usb-adb" && _usbSource is not null)
        {
            Log.Info("手机已断开 → 一并停止 shell 采集（两条通道联动）");
            StopUsbCapture();
        }
    }

    /// <summary>把所有手机踢下线（换配对码、停止接收时用）。</summary>
    public void DisconnectAll()
    {
        foreach (var session in _sessions)
        {
            session.Send(ControlMessages.Bye);
            session.Dispose();
            session.MarkOffline("已手动断开");
        }
    }

    // ------------------------------------------------------------ 网络地址

    private static IPAddress FindLocalAddress()
    {
        try
        {
            foreach (var ip in ListLocalAddresses().Select(IPAddress.Parse))
                return ip;
        }
        catch { }
        return IPAddress.Loopback;
    }

    public static string[] ListLocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    result.Add(addr.Address.ToString());
                }
            }
        }
        catch { }
        return result.ToArray();
    }

    public void Stop()
    {
        // ★ 先把手机采集端停掉，再管接收器自己。
        //
        //   原来这里没有这一步：关窗口 → MainWindow.OnClosing → Dispose() → Stop()，
        //   于是"正在采集时直接关掉程序"，手机上的 app_process 会一直留着，
        //   继续占着 AudioRecord(REMOTE_SUBMIX)，手机从此没声音。
        //   StopUsbCapture() 现在会顺手把手机端进程杀掉，所以放在这里兜底。
        //
        //   注意放在 IsRunning 判断【之前】：接收器没在跑、但采集在跑，也得清。
        try { StopUsbCapture(); } catch { }

        if (!IsRunning) return;
        IsRunning = false;
        try { _cts.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Close(); } catch { }
        _tcp = null;
        _udp = null;
        foreach (var session in _sessions) session.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _housekeeping.Dispose();
        _cts.Dispose();
    }
}
