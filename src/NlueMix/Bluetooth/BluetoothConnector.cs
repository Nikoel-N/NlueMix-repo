using NlueMix.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace NlueMix.Bluetooth;

/// <summary>
/// 音响连接助手。
///
/// 现实约束（必须说清楚）：
///  - Windows 没有开放"在程序里发起蓝牙配对"的公开接口，配对窗口只能由系统弹。
///  - 但"已经配对过的设备，让系统去连它"是能做到的，用的是 WinRT 的 ConnectAsync。
///  - 所以 NlueMix 的策略是：没配对过 → 带你去系统配对页；配对过 → 一键直接连。
/// </summary>
public static class BluetoothConnector
{
    /// <summary>让系统尝试连接某个已配对的蓝牙设备。</summary>
    public static async Task<bool> ConnectAsync(string mac, CancellationToken token = default)
    {
        string target = BluetoothScanner.StripMac(mac);
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector);

            foreach (var info in devices)
            {
                token.ThrowIfCancellationRequested();
                using var device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device is null) continue;

                string deviceMac = device.BluetoothAddress.ToString("x12");
                if (!string.Equals(deviceMac, target, StringComparison.OrdinalIgnoreCase)) continue;

                Log.Info("正在连接蓝牙设备: " + device.Name);
                var services = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                bool ok = services.Services.Count > 0;
                Log.Info(ok ? "已连接到 " + device.Name : "连接 " + device.Name + " 没有得到任何服务");
                return ok;
            }

            Log.Warn("没有找到已配对的设备: " + mac);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("连接蓝牙设备失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>打开 Windows 的蓝牙设置页，让用户完成配对。</summary>
    public static void OpenWindowsBluetoothSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("打开蓝牙设置失败: " + ex.Message);
        }
    }
}
