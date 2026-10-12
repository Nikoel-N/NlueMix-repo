using System.Text.RegularExpressions;
using NlueMix.Core;
using Microsoft.Win32;

namespace NlueMix.Bluetooth;

/// <summary>
/// 蓝牙音响识别。信息来源是 Windows 自己的配对数据库（注册表 BTHPORT\Devices），
/// 所以不需要额外驱动，也不需要管理员权限就能"看到"所有配对过的音响。
/// </summary>
public static partial class BluetoothScanner
{
    private const string DevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    /// <summary>蓝牙设备大类（Class of Device 的 major class 字段）。</summary>
    private const int MajorAudioVideo = 0x04;
    private const int MajorComputer = 0x01;
    private const int MajorPhone = 0x02;
    private const int MajorPeripheral = 0x05;

    [GeneratedRegex("^[0-9a-fA-F]{12}$")]
    private static partial Regex MacPattern();

    public static IReadOnlyList<SpeakerInfo> Scan()
    {
        var list = new List<SpeakerInfo>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DevicesKey);
            if (root is null)
            {
                Log.Warn("读不到蓝牙配对数据库，可能这台机器没有蓝牙适配器。");
                return list;
            }

            // 关键：这里必须关掉名字缓存，否则会形成
            // BluetoothScanner -> ListRenderDevices -> BluetoothNameCache -> Scan 的无限递归。
            var endpoints = Audio.AudioDevices.ListRenderDevices(resolveBluetoothByCache: false);

            foreach (var mac in root.GetSubKeyNames())
            {
                if (!MacPattern().IsMatch(mac)) continue;
                try
                {
                    using var deviceKey = root.OpenSubKey(mac);
                    if (deviceKey is null) continue;

                    string name = ReadName(deviceKey) ?? FormatMac(mac);
                    int cod = ReadClassOfDevice(deviceKey);
                    bool paired = ReadPairedFlag(deviceKey);
                    if (!IsSoundDevice(cod, name)) continue;

                    var info = new SpeakerInfo
                    {
                        Name = name,
                        Mac = FormatMac(mac),
                        IsPaired = paired,
                        DeviceId = "Bluetooth#" + mac.ToLowerInvariant(),
                    };
                    Enrich(info, endpoints);
                    list.Add(info);
                }
                catch (Exception ex)
                {
                    Log.Warn("解析蓝牙设备 " + mac + " 失败: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("扫描蓝牙设备失败: " + ex.Message);
        }

        return list
            .OrderByDescending(s => s.IsConnected)
            .ThenByDescending(s => s.IsPaired)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>把蓝牙设备和系统播放端点对上号，顺便判断当前是否真的连着。</summary>
    public static void Enrich(SpeakerInfo info, IReadOnlyList<Audio.AudioDevices.RenderDevice>? endpoints = null)
    {
        endpoints ??= Audio.AudioDevices.ListRenderDevices();

        var match = endpoints.FirstOrDefault(e => NameMatches(e.FriendlyName, info.Name) && e.IsActive)
                 ?? endpoints.FirstOrDefault(e => NameMatches(e.FriendlyName, info.Name));

        if (match is not null)
        {
            info.AudioEndpointName = match.FriendlyName;
            info.IsAudioEndpointPresent = true;
            info.IsConnected = match.IsActive;
            info.DeviceId ??= match.Id;
        }
        else
        {
            info.IsAudioEndpointPresent = false;
            info.IsConnected = false;
        }
    }

    private static bool NameMatches(string endpointName, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(endpointName) || string.IsNullOrWhiteSpace(deviceName)) return false;
        string a = Normalize(endpointName);
        string b = Normalize(deviceName);
        return a.Contains(b) || b.Contains(a);
    }

    private static string Normalize(string value)
    {
        string s = value.ToLowerInvariant();
        foreach (var noise in new[] { "bluetooth", "蓝牙", "stereo", "hands-free", "headset", "耳机", "音频", "audio", "(", ")", "-", "·" })
            s = s.Replace(noise, "");
        return s.Trim();
    }

    private static string? ReadName(RegistryKey key)
    {
        foreach (var field in new[] { "Name", "LEName", "FriendlyName" })
        {
            if (key.GetValue(field) is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            if (key.GetValue(field) is byte[] bytes && bytes.Length > 0)
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ');
                if (!string.IsNullOrWhiteSpace(text) && text.All(c => !char.IsControl(c))) return text;
            }
        }
        return null;
    }

    private static int ReadClassOfDevice(RegistryKey key)
    {
        try
        {
            if (key.GetValue("Class") is byte[] bytes && bytes.Length >= 3)
                return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
            if (key.GetValue("Class") is int value) return value;
        }
        catch { }
        return 0;
    }

    private static bool ReadPairedFlag(RegistryKey key)
    {
        try
        {
            // 配对过但暂时没连的设备，注册表里仍然有它的密钥和密钥材料
            return key.GetValue("MasterIRK") is not null
                || key.GetSubKeyNames().Contains("Authentication")
                || key.GetValue("Key") is not null
                || true; // 出现在这个数据库里本身就意味着配对过
        }
        catch { return true; }
    }

    /// <summary>按 Class of Device 判断是不是"出声的"设备。</summary>
    public static bool IsSoundDevice(int classOfDevice, string name)
    {
        int major = (classOfDevice >> 8) & 0x1F;
        int minor = (classOfDevice >> 2) & 0x3F;

        if (major == MajorComputer || major == MajorPhone) return false;
        if (major == MajorPeripheral)
        {
            // 外围设备里只有 0x01/0x02(键盘鼠标类) 明确排除，其余靠名字判断
            return LooksLikeAudioName(name);
        }
        if (major == MajorAudioVideo)
        {
            // 可穿戴耳机、免提、麦克风这些不是我们要的输出设备
            return minor switch
            {
                0x01 => false, // 可穿戴耳机
                0x02 => false, // 免提
                0x06 => false, // 麦克风
                0x07 => false, // 扬声器（蓝牙的"扬声器"是免提设备，音质差）
                _ => true,     // 耳机/音响/便携音频/车载/HiFi 等都算
            };
        }

        // 大类信息缺失（很多适配器不写这个字段），退化成看名字
        return LooksLikeAudioName(name);
    }

    private static bool LooksLikeAudioName(string name)
    {
        string lower = name.ToLowerInvariant();
        string[] hints =
        {
            "speaker", "sound", "audio", "music", "head", "phone", "buds", "pod", "box", "boom",
            "jbl", "bose", "sony", "marshall", "harman", "anker", "soundcore", "xiaomi", "edifier",
            "音响", "音箱", "耳机", "扬声器", "蓝牙",
        };
        return hints.Any(h => lower.Contains(h));
    }

    public static string FormatMac(string mac)
    {
        if (mac.Length != 12) return mac.ToUpperInvariant();
        return string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>反过来：AA:BB:.. -> aabbcc..</summary>
    public static string StripMac(string mac) => mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
}
