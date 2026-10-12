using System.Diagnostics;
using System.Net;
using System.Text;
using NlueMix.Core;
using NlueMix.Network;

namespace NlueMix.Audio;

/// <summary>
/// 无线（Wi-Fi）无损采集 —— 以 shell 身份运行 + REMOTE_SUBMIX 采集，传输走 NlueMix 自有协议。
///
/// <para><b>为什么这么绕：</b>在这台 Redmi K70 Ultra / Android 16 / HyperOS V816 上实测，
/// 普通 App 走 MediaProjection + AudioPlaybackCapture 只能读到纯静音，
/// 而 shell 身份 + REMOTE_SUBMIX 能正常录到声音。shell 权限只能靠 adb 拿到。</para>
///
/// <para><b>所以分成两段：</b></para>
/// <list type="number">
///   <item><b>点火（adb，一次性）</b>：adb 找到手机 → 推 nluemix-server.jar →
///         用 app_process 以 shell 身份启动采集端。这一步走 USB 或**无线调试**都行。</item>
///   <item><b>传音频（Wi-Fi，全程无线）</b>：采集端自己通过 Wi-Fi 连到电脑的控制端口，
///         按 NlueMix 协议推 **48 kHz 立体声 PCM16 无损** 流（约 1.5 Mbps）。
///         音频不经过 adb，也不做任何压缩。</item>
/// </list>
///
/// <para>之后电脑端完全按"一台普通手机"来对待它 —— 混音、让路、界面卡片全部复用。</para>
/// </summary>
public sealed class AdbCapture
{
    private const string JarResourceName = "nluemix-server.jar";
    private const string RemoteJarPath = "/data/local/tmp/nluemix-server.jar";
    private const string ServerClass = "com.nluemix.shell.CaptureServer";

    public string AdbPath { get; private set; } = "";
    public string DeviceSerial { get; private set; } = "";
    public string Status { get; private set; } = "";

    private static readonly string[] ExtraAdbCandidates =
    {
        @"D:\BlueMix-tools\android-sdk\platform-tools\adb.exe",
        @"C:\Android\Sdk\platform-tools\adb.exe",
    };

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
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
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

    /// <summary>当前 adb 认得的设备（USB 序列号或 ip:port）。</summary>
    public List<string> ListDevices()
    {
        var list = new List<string>();
        var r = RunAdb("devices", 15000);
        foreach (var raw in r.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith("*")) continue;
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Trim() == "device") list.Add(parts[0].Trim());
        }
        return list;
    }

    /// <summary>尝试用无线调试连上手机：先 mDNS 自动发现，再用给定的 IP。</summary>
    public bool EnsureDevice(string? phoneIp, out string message)
    {
        var adb = LocateAdb();
        if (adb is null)
        {
            message = "没有找到 adb.exe。把 platform-tools 里的 adb.exe 放进 NlueMix 目录即可。";
            Status = message;
            return false;
        }
        AdbPath = adb;
        RunAdb("start-server", 15000);

        var devices = ListDevices();
        if (devices.Count > 0)
        {
            // 同时插着线和无线时，优先用无线（这正是我们要的工作方式）
            DeviceSerial = devices.FirstOrDefault(d => d.Contains(':')) ?? devices[0];
            message = "已连接：" + DeviceSerial;
            Status = message;
            return true;
        }

        // 1) mDNS：Android 11+ 的"无线调试"会广播 _adb-tls-connect._tcp
        var mdns = RunAdb("mdns services", 8000);
        foreach (var raw in mdns.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of")) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var endpoint = parts[^1];
            if (endpoint.Contains(':') && !endpoint.StartsWith("["))
            {
                RunAdb("connect " + endpoint, 10000);
                devices = ListDevices();
                if (devices.Count > 0)
                {
                    DeviceSerial = devices[0];
                    message = "已通过无线调试自动连接：" + DeviceSerial;
                    Status = message;
                    return true;
                }
            }
        }

        // 2) 用户给的 IP：先试 5555（adb tcpip 5555 开的端口）
        if (!string.IsNullOrWhiteSpace(phoneIp))
        {
            var ip = phoneIp.Trim();
            foreach (var candidate in new[] { ip, ip + ":5555" })
            {
                if (!candidate.Contains(':')) continue;
                var res = RunAdb("connect " + candidate, 10000);
                devices = ListDevices();
                if (devices.Count > 0)
                {
                    DeviceSerial = devices[0];
                    message = "已连接：" + DeviceSerial;
                    Status = message;
                    return true;
                }
            }
            message = "连不上手机 " + ip + "。请确认手机和电脑在同一个 Wi-Fi，并且手机已开启「无线调试」。";
        }
        else
        {
            message = "没有检测到手机。请二选一：\n" +
                      "① 手机 → 设置 → 更多设置 → 开发者选项 → 打开「无线调试」（Android 11+，之后全程无线）；\n" +
                      "② 或者用数据线插一次，点「开启无线」后再拔掉线。";
        }

        Status = message;
        return false;
    }

    /// <summary>推服务端 + 无线启动（音频随后由手机主动连回电脑）。</summary>
    public bool StartWireless(string pcHost, int port, string pairCode, string serverName, out string message)
    {
        try
        {
            var jar = ExtractServerJar();
            var push = RunAdb($"-s {DeviceSerial} push \"{jar}\" {RemoteJarPath}", 40000);
            if (push.ExitCode != 0)
            {
                message = "推送服务端失败：" + push.Output.Trim();
                Status = message;
                return false;
            }

            // 先清掉可能残留的旧进程
            RunAdb($"-s {DeviceSerial} shell pkill -f {ServerClass}", 8000);

            var args = $"CLASSPATH={RemoteJarPath} nohup app_process /system/bin {ServerClass}"
                     + $" --host={pcHost} --port={port} --code={pairCode} --name=\"{serverName}\""
                     + " >/dev/null 2>&1 &";

            var launch = RunAdb($"-s {DeviceSerial} shell \"{args}\"", 20000);
            Log.Info("[无线采集] 启动输出: " + launch.Output.Trim());

            message = "已在手机上启动采集端（" + DeviceSerial + "），正在通过 Wi-Fi 回连 " + pcHost + ":" + port;
            Status = message;
            return true;
        }
        catch (Exception ex)
        {
            message = "启动无线采集失败：" + ex.Message;
            Status = message;
            Log.Error(message);
            return false;
        }
    }

    /// <summary>停掉手机上的采集端。</summary>
    public void StopRemote()
    {
        try { RunAdb($"-s {DeviceSerial} shell pkill -f {ServerClass}", 8000); } catch { }
    }

    /// <summary>USB 一步到位：adb tcpip 5555，方便之后拔线走无线。</summary>
    public bool EnableWirelessAdb(out string message)
    {
        if (AdbPath.Length == 0)
        {
            var adb = LocateAdb();
            if (adb is null) { message = "没有找到 adb.exe"; return false; }
            AdbPath = adb;
        }
        RunAdb("start-server", 15000);
        var devices = ListDevices();
        if (devices.Count == 0)
        {
            message = "没有插着数据线的手机。";
            return false;
        }
        DeviceSerial = devices[0];
        var r = RunAdb($"-s {DeviceSerial} tcpip 5555", 15000);
        message = r.Output.Trim();
        return r.ExitCode == 0;
    }

    /// <summary>
    /// 用手机自己上报的信息配对并连接。
    ///
    /// <p>这条路径解决的是「无线调试端口每次都会变」这个老大难：
    /// 手机端通过无障碍读出当前端口（必要时连配对码一起读出来），
    /// 经控制通道发给电脑，电脑只管执行 adb 命令 —— 用户不用手输任何东西。</p>
    ///
    /// <p>配对只需要做一次。配对成功后电脑的公钥就留在手机上了，
    /// 之后重启、重开无线调试都只需要 connect，不需要再 pair。</p>
    /// </summary>
    /// <param name="phoneIp">手机局域网 IP</param>
    /// <param name="connectPort">无线调试页面显示的连接端口</param>
    /// <param name="pairPort">配对端口；已经配对过就传 null</param>
    /// <param name="pairCode">6 位配对码；已经配对过就传 null</param>
    public bool PairAndConnect(string phoneIp, int connectPort, int? pairPort, string? pairCode, out string message)
    {
        if (AdbPath.Length == 0)
        {
            var adb = LocateAdb();
            if (adb is null) { message = "没有找到 adb.exe。请把 platform-tools 放进 NlueMix 目录，或安装 Android SDK。"; return false; }
            AdbPath = adb;
        }

        if (string.IsNullOrWhiteSpace(phoneIp) || connectPort <= 0)
        {
            message = "手机上报的地址不完整。";
            return false;
        }

        RunAdb("start-server", 15000);

        // 1) 需要的时候先配对
        if (pairPort is > 0 && !string.IsNullOrWhiteSpace(pairCode))
        {
            var pairEp = $"{phoneIp}:{pairPort.Value}";
            var pair = RunAdb($"pair {pairEp} {pairCode}", 30000);
            var pairOut = pair.Output.Trim();
            Log.Info("[自动配置] adb pair " + pairEp + " → " + pairOut);

            // 配对失败常见原因是"已经配对过了"，这不算致命，继续往下走 connect
            if (pair.ExitCode != 0 && !pairOut.Contains("Already paired", StringComparison.OrdinalIgnoreCase))
            {
                message = "配对失败：" + pairOut;
                return false;
            }
        }

        // 2) 连接
        var ep = $"{phoneIp}:{connectPort}";
        var conn = RunAdb($"connect {ep}", 20000);
        var connOut = conn.Output.Trim();
        Log.Info("[自动配置] adb connect " + ep + " → " + connOut);

        if (connOut.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            connOut.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            message = "连接失败：" + connOut +
                      "\n\n可能原因：无线调试被关掉了，或者端口又变了。请在手机上重新点一次自动配置。";
            return false;
        }

        // 3) 确认真的可用，并记下序列号
        var devices = ListDevices();
        var match = devices.FirstOrDefault(d => d.StartsWith(phoneIp + ":", StringComparison.Ordinal));
        if (match is null)
        {
            message = "命令执行了，但设备列表里没出现 " + ep + "。当前设备：" +
                      (devices.Count == 0 ? "（空）" : string.Join(", ", devices));
            return false;
        }

        DeviceSerial = match;
        message = "已连接 " + match;
        Log.Info("[自动配置] " + message);
        return true;
    }

    private static string ExtractServerJar()
    {
        var dirs = new List<string>();
        try
        {
            dirs.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NlueMix"));
        }
        catch { }
        try { dirs.Add(Path.Combine(Path.GetTempPath(), "NlueMix")); } catch { }
        try { dirs.Add(AppContext.BaseDirectory); } catch { }

        byte[] bytes;
        using (var stream = typeof(AdbCapture).Assembly.GetManifestResourceStream(JarResourceName))
        {
            if (stream is null)
                throw new InvalidOperationException("程序里没有嵌入 " + JarResourceName);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        foreach (var dir in dirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, JarResourceName);
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (Exception ex) { Log.Warn("写入服务端到 " + dir + " 失败: " + ex.Message); }
        }
        throw new InvalidOperationException("找不到可写目录释放 " + JarResourceName);
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
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>
    /// 给本机 IPv4 打分，分高的更可能是"手机能访问到的真实局域网地址"。
    /// 与 UI 里 MainWindow.ScoreAddress 的判定保持一致。
    /// </summary>
    private static int ScoreLanAddress(string ip)
    {
        var p = ip.Split('.');
        if (p.Length != 4 || !int.TryParse(p[0], out int a) || !int.TryParse(p[1], out int b)) return -1;
        if (a == 192 && b == 168) return 100;          // 家用路由器（最常见）
        if (a == 172 && b == 20) return 95;            // iPhone 热点
        if (a == 10) return 90;                        // 公司 / 学校网络
        if (a == 172 && b >= 16 && b <= 31) return 85; // 内网
        if (a == 169 && b == 254) return 10;           // 没连上网络
        if (a == 26 || a == 25 || a == 100) return 5;  // 虚拟网卡 / 运营商内网
        return 50;
    }

    /// <summary>挑一个能跟手机互通的电脑 IPv4 地址。</summary>
    public static string PickLocalAddress(string? phoneIp)
    {
        try
        {
            var all = NetworkReceiver.ListLocalAddresses();
            if (phoneIp is not null &&
                IPAddress.TryParse(phoneIp.Split(':')[0], out var phone))
            {
                foreach (var a in all)
                {
                    if (!IPAddress.TryParse(a, out var local)) continue;
                    var lb = local.GetAddressBytes();
                    var pb = phone.GetAddressBytes();
                    if (lb.Length == 4 && pb.Length == 4 && lb[0] == pb[0] && lb[1] == pb[1] && lb[2] == pb[2])
                        return a;
                }
            }
            // ★ 修复：没有手机地址可参考时，不能"返回第一个非回环 IPv4"。
            //   本机同时装着 Radmin VPN（26.236.x.x）和以太网（192.168.1.x），
            //   按网卡枚举顺序会先拿到 VPN 地址 —— 手机根本连不到它，
            //   拿它去 adb connect 也一定失败。
            //   这里改成按"最可能是真实局域网"的顺序挑：192.168 > 10 > 172.16-31，
            //   虚拟网卡段（26/25/100）和 169.254 排到最后。
            var candidates = all
                .Where(a => IPAddress.TryParse(a, out var ip)
                            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(ip))
                .OrderByDescending(ScoreLanAddress)
                .ToList();

            if (candidates.Count > 0) return candidates[0];
            return all.FirstOrDefault() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }
}

/// <summary>
/// 命令行验收：NlueMix.exe --wireless-test --seconds=20 [--phone=192.168.1.6]
///
/// 走**完全真实的链路**：adb 点火 → 手机端采集端通过 Wi-Fi 回连电脑控制端口 →
/// 电脑端 NetworkReceiver 按普通手机接进来 → 报出每路的包数 / 电平 / 峰值 / 是否无损。
/// 不需要界面，也不占用音箱。
/// </summary>
public static class WirelessTest
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        int seconds = 20;
        string? phone = null;
        bool noAdb = false;
        string? codeOverride = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(a["--seconds=".Length..], out seconds);
            else if (a.StartsWith("--phone=", StringComparison.OrdinalIgnoreCase))
                phone = a["--phone=".Length..];
            else if (a.StartsWith("--code=", StringComparison.OrdinalIgnoreCase))
                codeOverride = a["--code=".Length..];
            else if (string.Equals(a, "--no-adb", StringComparison.OrdinalIgnoreCase))
                noAdb = true;
        }

        Console.WriteLine("=== NlueMix 无线（Wi-Fi）无损采集验收 ===");

        var settings = SettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(phone)) settings.PhoneIp = phone!;
        if (!string.IsNullOrWhiteSpace(codeOverride)) settings.PairCode = codeOverride!;
        if (string.IsNullOrWhiteSpace(settings.PhoneIp)) phone = null;
        else phone = settings.PhoneIp;

        var pcIp = AdbCapture.PickLocalAddress(phone);
        Console.WriteLine("电脑地址 : " + pcIp + ":" + settings.Port);
        Console.WriteLine("连接码   : " + settings.PairCode);

        using var engine = new AudioEngine(settings);
        using var receiver = new NetworkReceiver(
            settings.Port, settings.Port + 1, settings.PairCode, engine.PhoneRings, _ => { });
        receiver.Start();
        Console.WriteLine("电脑端已在监听（音频不会被播放出来，只做电平统计）");

        var capture = new AdbCapture();
        if (noAdb)
        {
            // 只监听，不做 adb 点火 —— 用模拟手机脚本验证"静音保活"时用这个
            Console.WriteLine("（--no-adb：只监听，等外部客户端连进来）");
        }
        else
        {
            if (!capture.EnsureDevice(phone, out var message))
            {
                Console.WriteLine("× " + message);
                return 1;
            }
            Console.WriteLine("√ " + message);

            if (!capture.StartWireless(pcIp, settings.Port, settings.PairCode, "NlueMix Shell", out message))
            {
                Console.WriteLine("× " + message);
                return 1;
            }
            Console.WriteLine("√ " + message);
        }
        Console.WriteLine("等待手机通过 Wi-Fi 回连（最多 " + seconds + " 秒）…");
        Console.Out.Flush();

        double bestLevel = -96;
        long bestPackets = 0;
        string bestCodec = "";
        bool announced = false;

        for (int t = 0; t < seconds; t++)
        {
            Thread.Sleep(1000);
            for (int i = 0; i < AudioEngine.MaxPhones; i++)
            {
                var s = receiver.Slots[i];
                if (s.Packets <= 0) continue;
                if (!announced)
                {
                    announced = true;
                    Console.WriteLine("★ 手机已通过 Wi-Fi 接入：位置 " + (i + 1)
                        + "  " + s.DeviceName + "  " + s.CodecName
                        + "  " + s.SampleRate + " Hz/" + s.Channels + " ch");
                }
                if (t % 3 == 0 || s.LevelDb > bestLevel)
                {
                    Console.WriteLine("  [" + (t + 1).ToString().PadLeft(2) + "s] 包=" + s.Packets
                        + " 丢=" + s.LostPackets
                        + " 电平=" + s.LevelDb.ToString("0.0") + " dB"
                        + " 峰值=" + s.PeakDb.ToString("0.0") + " dB");
                }
                if (s.LevelDb > bestLevel) bestLevel = s.LevelDb;
                if (s.Packets > bestPackets) { bestPackets = s.Packets; bestCodec = s.CodecName; }
            }
        }

        if (!noAdb) capture.StopRemote();

        Console.WriteLine();
        if (bestPackets == 0)
        {
            Console.WriteLine("× 手机没有回连。检查：手机和电脑是否同一个 Wi-Fi；连接码是否正确。");
            return 1;
        }

        Console.WriteLine("总包数: " + bestPackets + "  编码: " + bestCodec + "  最大电平: " + bestLevel.ToString("0.0") + " dB");
        if (bestLevel > -60)
        {
            Console.WriteLine("=== 无线链路正常，而且采到了真实声音（无损 PCM）===");
            return 0;
        }
        Console.WriteLine("=== 无线链路通了，但这段时间手机没在放声音（电平接近静音）===");
        return 0;
    }
}
