using System.Diagnostics;
using NlueMix.Audio;
using NlueMix.Core;
using NlueMix.Network;

namespace NlueMix;

/// <summary>
/// 端到端验收：手机采集 → Wi-Fi/adb → 电脑 → WASAPI → 指定输出设备（蓝牙音箱）。
/// 不开界面，方便命令行验证整条链路。
///
/// 用法: NlueMix.exe --e2e-test --seconds=25 [--device=REDMI] [--phone=192.168.1.6]
/// </summary>
public static class E2EPlayTest
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        int seconds = 25;
        string? deviceFilter = null;
        string? phone = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(a["--seconds=".Length..], out seconds);
            else if (a.StartsWith("--device=", StringComparison.OrdinalIgnoreCase))
                deviceFilter = a["--device=".Length..];
            else if (a.StartsWith("--phone=", StringComparison.OrdinalIgnoreCase))
                phone = a["--phone=".Length..];
        }

        Console.WriteLine("=== NlueMix 端到端验收：手机 → Wi-Fi → 电脑 → WASAPI → 音箱 ===");

        var settings = SettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(phone)) settings.PhoneIp = phone!;
        phone = string.IsNullOrWhiteSpace(settings.PhoneIp) ? null : settings.PhoneIp;

        // ── 1) 选输出设备
        var devices = AudioDevices.ListRenderDevices();
        Console.WriteLine("可用输出设备：" + devices.Count + " 个");
        foreach (var d in devices)
            Console.WriteLine("   [" + (d.IsBluetooth ? "蓝牙" : "其他") + "] " + d.FriendlyName + "   " + d.StateText);

        AudioDevices.RenderDevice? pick = null;
        if (!string.IsNullOrWhiteSpace(deviceFilter))
            pick = devices.FirstOrDefault(d => d.FriendlyName.Contains(deviceFilter!, StringComparison.OrdinalIgnoreCase));
        pick ??= devices.FirstOrDefault(d => d.IsBluetooth);
        if (pick is null)
        {
            Console.WriteLine("× 没有找到可用的输出设备");
            return 1;
        }
        Console.WriteLine("★ 选中的输出设备：" + pick.FriendlyName + (pick.IsBluetooth ? "（蓝牙）" : ""));

        // ── 2) 起接收端 + 音频引擎
        using var engine = new AudioEngine(settings);
        using var receiver = new NetworkReceiver(
            settings.Port, settings.Port + 1, settings.PairCode, engine.PhoneRings, _ => { });
        receiver.Start();

        // ★ 关键接线（原来漏了，导致"电脑声音不让路"）：
        //   让路判定 Mixer.EvaluateDuck() 每 200ms 读的是各手机槽位的检测器电平。
        //   不把会话检测器接进混音器，引擎就永远认为"手机没在响"，于是从不让路。
        //   GUI 里 MainWindow 构造函数有这一句，命令行模式必须照做。
        for (int i = 0; i < AudioEngine.MaxPhones; i++)
            engine.AttachPhoneDetector(i, receiver.Slots[i].Detector);

        // startCapture:false —— 不把电脑系统声音录进来（让路靠 WASAPI 会话音量控制，不靠录音）
        engine.Start(pick.Id, 60, startCapture: false);
        Console.WriteLine("√ WASAPI 已启动（设备=" + pick.FriendlyName + "）");

        // ── 3) adb 点火：让手机上的 shell 采集端回连电脑
        var pcIp = AdbCapture.PickLocalAddress(phone);
        Console.WriteLine("电脑地址 : " + pcIp + ":" + settings.Port + "   连接码 " + settings.PairCode);

        var capture = new AdbCapture();
        if (!capture.EnsureDevice(phone, out var msg))
        {
            Console.WriteLine("× adb 设备不可用：" + msg);
            return 1;
        }
        Console.WriteLine("√ " + msg);

        if (!capture.StartWireless(pcIp, settings.Port, settings.PairCode, "NlueMix Shell", out msg))
        {
            Console.WriteLine("× 启动手机采集端失败：" + msg);
            return 1;
        }
        Console.WriteLine("√ " + msg);
        Console.WriteLine("等待手机回连并播放（" + seconds + " 秒）…");
        Console.Out.Flush();

        // ── 4) 跑一段时间并报告
        long firstPackets = -1;
        for (int t = 0; t < seconds; t++)
        {
            Thread.Sleep(1000);
            for (int i = 0; i < AudioEngine.MaxPhones; i++)
            {
                var s = receiver.Slots[i];
                if (s.Packets <= 0) continue;
                if (firstPackets < 0)
                {
                    firstPackets = s.Packets;
                    Console.WriteLine("★ 手机已接入：位置 " + (i + 1) + "  " + s.DeviceName
                        + "  " + s.CodecName + "  " + s.SampleRate + " Hz/" + s.Channels + " ch");
                }
                if (t % 3 == 0)
                    Console.WriteLine("  [" + (t + 1).ToString().PadLeft(2) + "s] 包=" + s.Packets
                        + " 丢=" + s.LostPackets
                        + " 电平=" + s.LevelDb.ToString("0.0") + " dB"
                        + " 峰值=" + s.PeakDb.ToString("0.0") + " dB"
                        + "  引擎=" + engine.State + " 缓冲=" + engine.OutputLatencyMs + "ms"
                        + "  让路=" + (engine.Mixer?.IsYielding == true ? "是" : "否"));
            }
        }

        Console.WriteLine("── 结果 ──");
        Console.WriteLine("输出设备  : " + pick.FriendlyName);
        Console.WriteLine("引擎状态  : " + engine.State);
        Console.WriteLine("输出格式  : " + engine.OutputFormat);
        Console.WriteLine("输出延迟  : " + engine.OutputLatencyMs + " ms");
        Console.WriteLine("=== 端到端运行结束 ===");

        try { capture.StopRemote(); } catch { }
        try { engine.Stop(); } catch { }
        return 0;
    }
}
