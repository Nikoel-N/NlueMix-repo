using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NlueMix.Audio;

/// <summary>播放设备枚举与识别。</summary>
public static class AudioDevices
{
    public sealed record RenderDevice(string Id, string FriendlyName, bool IsBluetooth, bool IsActive, string StateText, string Format);

    /// <summary>
    /// 名字里带这些词的端点，通常是蓝牙/免提那一路（音质差，要避开）。
    /// </summary>
    private static readonly string[] HandsFreeHints = { "hands-free", "免提", "headset", "通话" };

    /// <param name="activeOnly">只列正在工作的设备</param>
    /// <param name="resolveBluetoothByCache">
    /// 是否要借助"已配对蓝牙设备名"来判断某个端点是不是蓝牙音响。
    /// 注意：蓝牙扫描自己会调用本方法，所以扫描期间必须传 false，否则会形成无限递归。
    /// </param>
    public static IReadOnlyList<RenderDevice> ListRenderDevices(bool activeOnly = false, bool resolveBluetoothByCache = true)
    {
        var result = new List<RenderDevice>();
        try
        {
            var bluetoothNames = resolveBluetoothByCache
                ? BluetoothNameCache.Get()
                : (IReadOnlyCollection<string>)Array.Empty<string>();

            using var enumerator = new MMDeviceEnumerator();
            var state = activeOnly ? DeviceState.Active : DeviceState.All;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, state))
            {
                try
                {
                    string id = device.ID;
                    string name = device.FriendlyName;
                    bool active = device.State == DeviceState.Active;
                    bool bt = IsBluetoothEndpoint(id, name, bluetoothNames);

                    string fmt = "-";
                    if (active)
                    {
                        try { fmt = Describe(device.AudioClient.MixFormat); } catch { }
                    }
                    result.Add(new RenderDevice(id, name, bt, active, StateText(device.State), fmt));
                }
                catch { /* 单个设备出错不影响整体枚举 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举播放设备失败: " + ex.Message);
        }
        return result;
    }

    public static MMDevice? Resolve(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        // 第一选择：直接当合法的音频端点 ID 用
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId);
        }
        catch
        {
            // 继续尝试别的办法
        }

        // 第二选择："Bluetooth#MAC" 这种合成 ID 不是音频端点 ID，改用名字去匹配
        try
        {
            if (deviceId.StartsWith("Bluetooth#", StringComparison.OrdinalIgnoreCase))
            {
                string mac = deviceId["Bluetooth#".Length..].Replace(":", "").Replace("-", "");
                if (mac.Length == 12)
                {
                    string? targetName = NameOfPairedDevice(mac);
                    if (!string.IsNullOrWhiteSpace(targetName))
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        {
                            if (NamesMatch(device.FriendlyName, targetName)) return device;
                        }
                    }
                    Log.Info("蓝牙地址 " + mac + " 没有对应的可用音频端点，改用系统默认播放设备");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("按蓝牙地址匹配音频端点失败: " + ex.Message);
        }

        return null;
    }

    /// <summary>从已配对蓝牙设备里按 MAC 找名字。注意不要再触发名字缓存，避免递归。</summary>
    private static string? NameOfPairedDevice(string macNoColon)
    {
        try
        {
            foreach (var speaker in Bluetooth.BluetoothScanner.Scan())
            {
                string candidate = (speaker.Mac ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();
                if (candidate == macNoColon.ToLowerInvariant()) return speaker.Name;
            }
        }
        catch { }
        return null;
    }

    public static MMDevice? DefaultRenderDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Warn("取默认播放设备失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 判断一个播放端点是不是蓝牙音响。
    /// 光看设备 ID 不靠谱（Windows 11 的蓝牙端点 ID 里根本没有 "bth"），
    /// 所以主要依据是"这个端点的名字能不能和某台已配对的蓝牙设备对上"。
    /// </summary>
    public static bool IsBluetoothEndpoint(string id, string name, IReadOnlyCollection<string>? knownBluetoothNames = null)
    {
        string lowerId = id.ToLowerInvariant();
        if (lowerId.Contains("bth") || lowerId.Contains("bluetooth")) return true;
        if (name.Contains("蓝牙") || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return true;

        knownBluetoothNames ??= BluetoothNameCache.Get();
        return knownBluetoothNames.Any(btName => NamesMatch(name, btName));
    }

    /// <summary>两台设备的显示名是否指的是同一个东西（容忍 Windows 加的前后缀）。</summary>
    public static bool NamesMatch(string a, string b)
    {
        string x = Normalize(a);
        string y = Normalize(b);
        if (x.Length < 3 || y.Length < 3) return false;
        return x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        string s = value.ToLowerInvariant();
        foreach (var noise in new[]
                 {
                     "bluetooth", "蓝牙", "stereo", "hands-free", "headset", "耳机", "音频", "audio",
                     "输出", "扬声器", "speaker", "(", ")", "（", "）", "-", "_", "·", " ",
                 })
            s = s.Replace(noise, "");
        return s.Trim();
    }

    /// <summary>是不是那种音质很差的"免提/通话"端点。</summary>
    public static bool IsHandsFree(string name)
        => HandsFreeHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

    public static string StateText(DeviceState state) => state switch
    {
        DeviceState.Active => "可用",
        DeviceState.Disabled => "已禁用",
        DeviceState.NotPresent => "未接入",
        DeviceState.Unplugged => "已拔出",
        _ => state.ToString(),
    };

    public static string Describe(WaveFormat format)
        => format.SampleRate / 1000.0 + " kHz · " + format.Channels + " ch · " + format.BitsPerSample + " bit";
}

/// <summary>
/// 已配对蓝牙设备名字的缓存。每次枚举音频端点都去读注册表太慢，缓存 10 秒。
/// </summary>
internal static class BluetoothNameCache
{
    private static readonly object Gate = new();
    private static IReadOnlyCollection<string> _names = Array.Empty<string>();
    private static long _stamp;
    [ThreadStatic] private static bool _busy;

    public static IReadOnlyCollection<string> Get()
    {
        // 防重入：万一将来又出现环形依赖，这里直接返回空集合，绝不再递归下去
        if (_busy) return Array.Empty<string>();

        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_names.Count > 0 && now - _stamp < 10_000) return _names;

            _busy = true;
            try
            {
                _names = Bluetooth.BluetoothScanner.Scan().Select(s => s.Name).ToArray();
                _stamp = now;
            }
            catch
            {
                // 扫描失败就沿用旧结果
            }
            finally
            {
                _busy = false;
            }
            return _names;
        }
    }

    public static void Invalidate()
    {
        lock (Gate) { _names = Array.Empty<string>(); _stamp = 0; }
    }
}
