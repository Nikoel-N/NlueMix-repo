using System.Diagnostics;
using System.Text;
using NlueMix.Core;

namespace NlueMix.Audio;

/// <summary>
/// USB（adb）直连采集 —— 以 shell 身份运行 + REMOTE_SUBMIX，Android 平台的公开技术路径。
///
/// 【为什么需要它】
/// 在 Redmi K70 Ultra / Android 16 / HyperOS V816 上实测：
///   · 普通 App（MediaProjection + AudioPlaybackCapture）读到的永远是纯静音（峰值恒为 0）；
///   · 同一时刻 scrcpy 用 adb shell 身份 + REMOTE_SUBMIX 录像，PEAK=32154、89% 非零采样，是真实声音。
/// 也就是说这台 ROM 只挡住了"App 走 MediaProjection"这条路，没有挡住系统内部采集。
///
/// 【原理】
///  1. 把一个只含 classes.dex 的 nluemix-server.jar 推到 /data/local/tmp/；
///  2. 用 app_process 以 shell 身份运行它 —— shell 用户持有签名权限
///     android.permission.CAPTURE_AUDIO_OUTPUT，可以直接读 REMOTE_SUBMIX；
///  3. 服务端把 48 kHz / 立体声 / PCM16 原始音频写到标准输出；
///  4. 本类用 adb exec-out 把它读回来，按 20 ms 一帧交给手机槽位，后面完全复用原有混音/让路逻辑。
///
/// 实测（本机、B 站在播放）：PEAK=32580，RMS=8579.8，89.4% 非零采样。
/// </summary>
public sealed class UsbAudioSource : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 960;                       // 20 ms
    public const int FrameBytes = FrameSamples * Channels * 2; // 3840

    private const string JarResourceName = "nluemix-server.jar";
    private const string RemoteJarPath = "/data/local/tmp/nluemix-server.jar";
    private const string ServerClass = "com.nluemix.shell.CaptureServer";

    private Process? _process;
    private Thread? _reader;
    private volatile bool _running;
    private readonly byte[] _frame = new byte[FrameBytes];

    /// <summary>每收到一帧 20 ms 的 PCM16 触发一次（在后台线程上）。</summary>
    public event Action<byte[], int>? Frame;
    public event Action<string>? Failed;
    public event Action? Stopped;

    public bool IsRunning => _running;
    public string AdbPath { get; private set; } = "";
    public string DeviceSerial { get; private set; } = "";

    private static readonly string[] ExtraAdbCandidates =
    {
        @"D:\BlueMix-tools\android-sdk\platform-tools\adb.exe",
        @"C:\Android\Sdk\platform-tools\adb.exe",
    };

    /// <summary>找一个可用的 adb：程序目录 → 常见安装位置 → PATH。</summary>
    public static string? LocateAdb()
    {
        var candidates = new List<string>();
        try { candidates.Add(Path.Combine(AppContext.BaseDirectory, "adb.exe")); } catch { }
        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"));
        }
        catch { }
        candidates.AddRange(ExtraAdbCandidates);

        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';'))
            {
                var d = dir.Trim();
                if (d.Length > 0) candidates.Add(Path.Combine(d, "adb.exe"));
            }
        }
        catch { }

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// <summary>启动采集。返回 false 表示启动失败（原因见 <see cref="LastError"/>）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 探一下这个地址在不在网上（3 次 ICMP，总计最多约 1.5 秒）。
    /// 用来区分「手机没连网」和「手机在线但无线调试关了」这两种完全不同的故障。
    /// </summary>
    private static bool IsHostReachable(string host)
    {
        try
        {
            // 去掉可能的 :port 后缀
            var h = host;
            int colon = h.IndexOf(':');
            if (colon > 0) h = h.Substring(0, colon);

            using var ping = new System.Net.NetworkInformation.Ping();
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var reply = ping.Send(h, 500);
                    if (reply != null && reply.Status == System.Net.NetworkInformation.IPStatus.Success) return true;
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="phoneIp">
    /// 手机的局域网 IP（手机走 Wi-Fi 时用）。为空则先看有没有 USB/已连接的 adb 设备，
    /// 再尝试用 mDNS 自动发现「无线调试」设备。
    /// </param>
    public bool Start(string? phoneIp = null)
    {
        try
        {
            var adb = LocateAdb();
            if (adb is null)
            {
                LastError = "没有找到 adb.exe。请把 platform-tools 放进 NlueMix 目录，或安装 Android SDK。";
                Failed?.Invoke(LastError);
                return false;
            }
            AdbPath = adb;

            // 1) 服务器（adb start-server / 选择设备）
            RunAdb("start-server", 15000);
            DeviceSerial = PickDevice();

            // 1.1) 没有设备就尝试走 Wi-Fi：adb connect <手机IP>:5555
            //
            // 注意这条路径非常适合"手机走 Wi-Fi、电脑走有线以太网"的组网：
            //   · 是【电脑主动连手机】，音频流沿同一条 TCP 连接回来，
            //     所以电脑侧【不需要开放任何入站端口】，也不用管 Windows 防火墙；
            //   · 手机和电脑只要在同一个局域网（例如都是 192.168.1.x）就行。
            if (DeviceSerial.Length == 0 && !string.IsNullOrWhiteSpace(phoneIp))
            {
                var endpoint = NormalizeEndpoint(phoneIp!);
                var connect = RunAdb("connect " + endpoint, 15000);
                Log.Info("[手机采集] adb connect " + endpoint + " → " + connect.Output.Trim());
                DeviceSerial = PickDevice();
            }

            // 1.2) 还是没有设备，就用 mDNS 找「无线调试」广播的设备
            if (DeviceSerial.Length == 0) DeviceSerial = TryConnectViaMdns();

            // 1.3) 再兜底：在局域网里扫 5555 端口（免手输手机 IP）
            if (DeviceSerial.Length == 0) DeviceSerial = TrySubnetScan();

            if (DeviceSerial.Length == 0)
            {
                // 分情况给提示。实测最常见的一种失败是：
                //   手机在网上（ping 得通），但「无线调试」被系统关掉了 ——
                //   这时 adb 设备列表空、mDNS 也发现不到，用户看到的却是一句笼统的
                //   「没有检测到手机」，根本不知道去哪儿修。
                //   所以这里先探一下手机在不在网上，再决定说什么。
                bool phoneOnline = !string.IsNullOrWhiteSpace(phoneIp) && IsHostReachable(phoneIp!);

                LastError = phoneOnline
                        ? "手机 " + phoneIp + " 在线，但连不上它的调试端口。\n" +
                          "说明「无线调试」被关掉了（息屏或系统清理都可能把它关掉）。\n\n" +
                          "请在手机上重新打开：\n" +
                          "    设置 → 更多设置 → 开发者选项 → 无线调试 → 打开\n\n" +
                          "打开后不用再做什么，回到这里重新点采集即可。"
                        : "没有检测到手机。请二选一：\n" +
                          "① 手机打开「开发者选项 → 无线调试」（Android 11+，之后全程无线）；\n" +
                          "② 或用数据线插一次并允许 USB 调试。";

                Log.Warn("[手机采集] " + LastError.Replace("\n", " "));
                Failed?.Invoke(LastError);
                return false;
            }

            // 2) 释放服务端
            var jar = ExtractServerJar();
            var push = RunAdb($"-s {DeviceSerial} push \"{jar}\" {RemoteJarPath}", 30000);
            if (push.ExitCode != 0)
            {
                LastError = "推送服务端失败：" + push.Output.Trim();
                Failed?.Invoke(LastError);
                return false;
            }

            // 3) 起采集进程，音频从它的 stdout 出来
            var psi = new ProcessStartInfo
            {
                FileName = adb,
                Arguments = $"-s {DeviceSerial} exec-out CLASSPATH={RemoteJarPath} app_process /system/bin {ServerClass}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            _process = Process.Start(psi);
            if (_process is null)
            {
                LastError = "无法启动 adb 采集进程。";
                Failed?.Invoke(LastError);
                return false;
            }

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("[USB采集] " + e.Data);
            };
            _process.BeginErrorReadLine();

            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "NlueMix-UsbAudio" };
            _reader.Start();

            Log.Info($"手机采集已启动：{DeviceSerial}"
                + (DeviceSerial.Contains(':') ? "（Wi-Fi）" : "（USB）")
                + " —— adb exec-out + REMOTE_SUBMIX，48 kHz 立体声 PCM16 无损");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("启动 USB 采集失败: " + ex);
            Failed?.Invoke(LastError);
            return false;
        }
    }

    /// <summary>单个音频帧最多允许多少采样（防御坏包头，1 秒足够）。</summary>
    private const int MaxSamplesPerFrame = 48000;

    /// <summary>一帧payload 的最大字节数。</summary>
    private static readonly int MaxPayloadBytes = MaxSamplesPerFrame * Channels * 2;

    /// <summary>把 stream 读满 count 字节；中途 EOF 或出错返回 false。</summary>
    private static bool FillExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n;
            try { n = stream.Read(buffer, offset + got, count - got); }
            catch { return false; }
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private void ReadLoop()
    {
        try
        {
            var stdout = _process?.StandardOutput.BaseStream;
            if (stdout is null) return;

            // 按 NlueMix 帧格式解析（16 字节小端头 + PCM16），不再按固定长度硬切。
            //
            // ★ 实测根因（hexdump 证据，2026-10-13）：
            //   即使服务端写的是 System.err.println，在 app_process 下 stderr 会和 stdout
            //   合并到同一个 fd —— 于是启动横幅那 229 字节文本混进了 PCM 流
            //   （内容形如 nluemix-server: rate=48000 host=null port=47820）。
            //   旧解析器按固定 3840 字节硬切帧，从第 229 字节起就【永久错位】，
            //   左右声道被拆开重新配对，听感是持续风噪 + 音质极差，且永不自愈。
            //   实测第一个 magic 出现在偏移 229，之后帧间距恒为 3856 = 16 + 960*4，完全规整。
            //   所以下面的滑动重同步不是"防御性代码"，它是这条链路能用的前提。
            //
            // 为什么必须这样：adb exec-out 是裸字节流。原来的写法每读满 3840 字节就当成一帧，
            // 只要流里丢/多一个字节，后面【每一帧都是错位的】—— 左右声道被拆开重新配对，
            // 听感是持续风噪、完全听不出原声，而且永远不恢复。
            // 现在靠包头 magic 做滑动重同步：错位之后最多滑 15 字节就能重新对上。
            var header = new byte[16];
            var payload = new byte[MaxPayloadBytes];
            long resyncs = 0, badHeaders = 0;
            long lastReport = Environment.TickCount64;

            while (_running)
            {
                // ── 读 16 字节包头 ──
                if (!FillExact(stdout, header, 0, 16)) break;

                // ── 滑动重同步：magic 不对就丢 1 字节再补 1 字节，直到对上 ──
                while (BitConverter.ToUInt32(header, 0) != NlueMix.Network.Protocol.Magic)
                {
                    resyncs++;
                    Buffer.BlockCopy(header, 1, header, 0, 15);
                    if (!FillExact(stdout, header, 15, 1)) { _running = false; break; }

                    // 失步严重时打一条，便于判断链路质量
                    long now = Environment.TickCount64;
                    if (now - lastReport >= 5000)
                    {
                        lastReport = now;
                        Log.Warn("[USB采集] 帧同步丢失，已滑动重同步 " + resyncs + " 次");
                    }
                }
                if (!_running) break;

                int samples = BitConverter.ToUInt16(header, 12);
                int bytes = samples > 0 ? samples * Channels * 2 : 0;
                if (bytes <= 0 || bytes > payload.Length)
                {
                    // 头不可信（多半是碰巧撞上 magic 的巧合），往后滑动 1 字节继续找
                    badHeaders++;
                    Buffer.BlockCopy(header, 1, header, 0, 15);
                    if (!FillExact(stdout, header, 15, 1)) break;
                    continue;
                }

                if (!FillExact(stdout, payload, 0, bytes)) break;
                Frame?.Invoke(payload, bytes);
            }

            if (resyncs > 0 || badHeaders > 0)
                Log.Warn("[USB采集] 本次会话：滑动重同步 " + resyncs + " 次，坏包头 " + badHeaders + " 个");
        }
        catch (Exception ex)
        {
            if (_running) Log.Warn("USB 采集读取中断: " + ex.Message);
        }
        finally
        {
            var wasRunning = _running;
            _running = false;
            if (wasRunning) Log.Warn("USB 采集流已结束");
            try { Stopped?.Invoke(); } catch { }
        }
    }

    /// <summary>把 "192.168.1.6" 补成 "192.168.1.6:5555"。</summary>
    private static string NormalizeEndpoint(string ip)
    {
        var v = ip.Trim();
        return v.Contains(':') ? v : v + ":5555";
    }

    /// <summary>用 mDNS 找 Android 11+「无线调试」广播的 _adb-tls-connect 服务并连上。</summary>
    private string TryConnectViaMdns()
    {
        try
        {
            var r = RunAdb("mdns services", 8000);
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith("*")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                var endpoint = parts[^1];
                if (!endpoint.Contains(':') || endpoint.StartsWith("[")) continue;

                var c = RunAdb("connect " + endpoint, 10000);
                Log.Info("[手机采集] mDNS 发现 " + endpoint + " → " + c.Output.Trim());
                var found = PickDevice();
                if (found.Length > 0) return found;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("mDNS 查找手机失败: " + ex.Message);
        }
        return "";
    }

    /// <summary>是不是私有局域网地址（10.x / 172.16-31.x / 192.168.x）。</summary>
    private static bool IsPrivateLan(byte[] b)
    {
        if (b[0] == 10) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        return false;
    }

    /// <summary>
    /// 兜底：在电脑所在的局域网里扫一遍 adb 的 5555 端口，找到手机就 adb connect。
    /// 手机走 Wi-Fi、电脑走有线时，两者在同一个 /24 网段，这一步能省掉"手输手机 IP"。
    /// </summary>
    private string TrySubnetScan()
    {
        try
        {
            foreach (var localIp in NlueMix.Network.NetworkReceiver.ListLocalAddresses())
            {
                if (!System.Net.IPAddress.TryParse(localIp, out var addr)) continue;
                if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(addr)) continue;

                var b = addr.GetAddressBytes();
                if (!IsPrivateLan(b)) continue;
                var prefix = b[0] + "." + b[1] + "." + b[2] + ".";

                var hits = new List<string>();
                var tasks = new List<System.Threading.Tasks.Task>();
                for (int h = 1; h <= 254; h++)
                {
                    var host = h;
                    tasks.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using var client = new System.Net.Sockets.TcpClient();
                            var ar = client.BeginConnect(prefix + host, 5555, null, null);
                            if (ar.AsyncWaitHandle.WaitOne(220) && client.Connected)
                                lock (hits) hits.Add(prefix + host + ":5555");
                        }
                        catch { }
                    }));
                }
                try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), 6000); } catch { }

                foreach (var endpoint in hits)
                {
                    var c = RunAdb("connect " + endpoint, 10000);
                    Log.Info("[手机采集] 局域网扫描发现 " + endpoint + " → " + c.Output.Trim());
                    var found = PickDevice();
                    if (found.Length > 0) return found;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("扫描局域网手机失败: " + ex.Message);
        }
        return "";
    }

    private string PickDevice()
    {
        var r = RunAdb("devices", 15000);
        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of")) continue;
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Trim() == "device") return parts[0].Trim();
        }
        return "";
    }

    private static string ExtractServerJar()
    {
        // 依次尝试几个可写目录，哪个能写就用哪个（权限受限的环境下也能跑起来）
        var dirs = new List<string>();
        try
        {
            dirs.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NlueMix"));
        }
        catch { }
        try { dirs.Add(Path.Combine(Path.GetTempPath(), "NlueMix")); } catch { }
        try { dirs.Add(AppContext.BaseDirectory); } catch { }

        var bytes = ReadEmbeddedJar();

        foreach (var dir in dirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, JarResourceName);
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("写入服务端到 " + dir + " 失败（换下一个）: " + ex.Message);
            }
        }

        throw new InvalidOperationException("找不到可写的目录来释放 " + JarResourceName);
    }

    private static byte[] ReadEmbeddedJar()
    {
        using var stream = typeof(UsbAudioSource).Assembly.GetManifestResourceStream(JarResourceName);
        if (stream is null)
            throw new InvalidOperationException("程序里没有嵌入 " + JarResourceName + "（构建时漏了 EmbeddedResource）");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private (int ExitCode, string Output) RunAdb(string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // ★ adb.exe 在 Windows 上输出的是 UTF-8 ——
                //   实测原始字节：E7 94 B1 就是「由」的 UTF-8 编码。
                //   而 .NET 在没指定编码时按系统 OEM 代码页解码（中文系统是 GBK），
                //   于是「由于目标计算机积极拒绝，无法连接」会变成「鐢变簬鐩爣璁＄畻鏈虹Н鏋佹嫆缁濓紝…」。
                //   这个乱码会出现在所有 adb 提示里（配对失败、连接失败、设备列表），
                //   用户根本读不懂，必须显式指定。
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "启动 adb 失败");
            var sb = new StringBuilder();
            sb.Append(p.StandardOutput.ReadToEnd());
            sb.Append(p.StandardError.ReadToEnd());
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "adb 超时：" + args);
            }
            return (p.ExitCode, sb.ToString());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public void Stop()
    {
        _running = false;
        try { _process?.StandardInput.Close(); } catch { }
        try { if (_process is not null && !_process.HasExited) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// 命令行自检：NlueMix.exe --usb-test --seconds=10 --out=usb.wav
/// 用 USB（adb）通道录一段手机声音存成 WAV 并打印峰值，用来验证"到底能不能采到声音"。
/// </summary>
public static class UsbCaptureTest
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        int seconds = 10;
        string outPath = Path.Combine(AppContext.BaseDirectory, "usb-capture-test.wav");
        foreach (var a in args)
        {
            if (a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(a["--seconds=".Length..], out seconds);
            else if (a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                outPath = a["--out=".Length..];
        }

        Console.WriteLine("=== NlueMix USB（adb）采集自检 ===");

        var adb = UsbAudioSource.LocateAdb();
        Console.WriteLine("adb: " + (adb ?? "× 没找到"));
        if (adb is null) return 1;

        var pcm = new MemoryStream();
        using var source = new UsbAudioSource();
        source.Frame += (buffer, count) => pcm.Write(buffer, 0, count);

        if (!source.Start())
        {
            Console.WriteLine("× 启动失败: " + (source.LastError ?? "未知"));
            return 1;
        }

        Console.WriteLine("设备: " + source.DeviceSerial);
        Console.WriteLine("采集 " + seconds + " 秒…");
        Thread.Sleep(seconds * 1000);
        source.Stop();

        var data = pcm.ToArray();
        if (data.Length == 0)
        {
            Console.WriteLine("× 一帧都没收到");
            return 1;
        }

        int peak = 0;
        long sum = 0;
        int n = 0, nonZero = 0;
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            short s = (short)(data[i] | (data[i + 1] << 8));
            int a = Math.Abs((int)s);
            if (a > peak) peak = a;
            if (s != 0) nonZero++;
            sum += (long)s * s;
            n++;
        }
        double rms = n > 0 ? Math.Sqrt((double)sum / n) : 0;

        WriteWav(outPath, data);
        Console.WriteLine("字节: " + data.Length + "  采样: " + n);
        Console.WriteLine("PEAK=" + peak + "  RMS=" + rms.ToString("0.0")
            + "  非零采样=" + (100.0 * nonZero / Math.Max(1, n)).ToString("0.00") + "%");
        Console.WriteLine("WAV: " + outPath);
        Console.WriteLine(peak > 0 ? "=== 采到真实声音了 ===" : "=== 全是静音（手机当时没在放声音？）===");
        return peak > 0 ? 0 : 2;
    }

    private static void WriteWav(string path, byte[] pcm)
    {
        try
        {
            int rate = UsbAudioSource.SampleRate, ch = UsbAudioSource.Channels;
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            int byteRate = rate * ch * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + pcm.Length);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)ch);
            w.Write(rate);
            w.Write(byteRate);
            w.Write((short)(ch * 2));
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        catch (Exception ex)
        {
            Console.WriteLine("写 WAV 失败: " + ex.Message);
        }
    }
}
