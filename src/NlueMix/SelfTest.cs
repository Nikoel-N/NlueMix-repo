using NlueMix.Audio;
using NlueMix.Bluetooth;
using NlueMix.Core;
using NlueMix.Network;

namespace NlueMix;

/// <summary>
/// 命令行自检：NlueMix.exe --selftest
/// 不开界面，把关键环节跑一遍并打印结果，方便排查"为什么没声音"。
/// </summary>
public static class SelfTest
{
    public static int Run(string[] args)
    {
        // 控制台被重定向时设置编码会抛异常，这里的目的是"能打印就打印"
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Console.WriteLine("=== NlueMix 自检 ===");
        Console.WriteLine();

        int problems = 0;

        // 1) 蓝牙音响
        Console.WriteLine("[1] 蓝牙音响扫描");
        var speakers = BluetoothScanner.Scan();
        if (speakers.Count == 0)
        {
            Console.WriteLine("    × 没有找到任何蓝牙音响设备");
            Console.WriteLine("      请先在 Windows 设置里配对一次音响。");
            problems++;
        }
        foreach (var speaker in speakers)
        {
            Console.WriteLine($"    {(speaker.IsConnected ? "●" : "○")} {speaker.DisplayName}");
            Console.WriteLine($"        MAC: {speaker.Mac}  状态: {speaker.StatusText}");
            if (speaker.AudioEndpointName is not null)
                Console.WriteLine($"        音频端点: {speaker.AudioEndpointName}");
        }
        Console.WriteLine();

        // 2) 播放设备
        Console.WriteLine("[2] 播放设备");
        var devices = AudioDevices.ListRenderDevices();
        foreach (var device in devices)
        {
            Console.WriteLine($"    {(device.IsActive ? "●" : "○")} {device.FriendlyName}");
            Console.WriteLine($"        {(device.IsBluetooth ? "蓝牙" : "非蓝牙")} · {device.StateText} · {device.Format}");
        }
        Console.WriteLine();

        // 3) 网络
        Console.WriteLine("[3] 网络监听地址");
        foreach (var address in NetworkReceiver.ListLocalAddresses())
            Console.WriteLine("    " + address + "  （控制端口 47820 / 音频端口 47821）");
        Console.WriteLine();

        // 4) 引擎
        var settings = SettingsStore.Load();
        Console.WriteLine("[4] 音频引擎启动测试");
        Console.WriteLine("    连接码: " + settings.PairCode);
        using var engine = new AudioEngine(settings);
        try
        {
            var target = speakers.FirstOrDefault(s => s.IsConnected);
            string? deviceId = settings.RenderDeviceId ?? target?.DeviceId;
            engine.Start(deviceId, settings.TargetBufferMs, startCapture: true);
            Thread.Sleep(1500);
            Console.WriteLine("    √ 引擎已启动");
            Console.WriteLine("      输出设备: " + engine.OutputDeviceName);
            Console.WriteLine("      输出格式: " + engine.OutputFormat);
            Console.WriteLine("      采集设备: " + engine.CaptureDeviceName);
            Console.WriteLine("      采集格式: " + engine.CaptureFormat);
            Console.WriteLine("      输出缓冲: " + engine.OutputBufferLatencyMs + " ms");

            Thread.Sleep(1500);
            var stats = engine.Snapshot();
            Console.WriteLine("      运行状态: " + stats.State + "，缓冲 " + stats.BufferLatencyMs + " ms，欠载 " + stats.UnderrunFrames);

            if (engine.SystemSource is not null)
                Console.WriteLine("      本机声音电平: " + engine.SystemSource.PeakDb.ToString("0.0") + " dB"
                    + (engine.SystemSource.IsProducing ? "（正在播放）" : "（当前安静）"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("    × 引擎启动失败: " + ex.Message);
            problems++;
        }
        finally
        {
            engine.Stop();
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "=== 自检通过 ===" : $"=== 自检发现 {problems} 个问题 ===");
        Console.WriteLine("日志: " + Log.LogPath);
        return problems == 0 ? 0 : 1;
    }
}
