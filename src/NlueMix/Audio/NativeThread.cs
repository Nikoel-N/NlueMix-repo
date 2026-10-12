using System.Runtime.InteropServices;

namespace NlueMix.Audio;

/// <summary>
/// 把当前线程注册成"多媒体流线程"（MMCSS），并在结束时还原。
/// 这是避免爆音/卡顿最有效的一招：Windows 会为音频线程预留 CPU 时间片。
/// 注册失败也不影响功能，只是没这个优待。
/// </summary>
public sealed class MmcssScope : IDisposable
{
    private readonly IntPtr _handle;
    private readonly bool _ok;

    public MmcssScope(string task = "Audio", int priority = 2)
    {
        try
        {
            uint taskIndex = 0;
            _handle = AvSetMmThreadCharacteristicsW(task, ref taskIndex);
            _ok = _handle != IntPtr.Zero;
            if (_ok) AvSetMmThreadPriority(_handle, priority);
        }
        catch
        {
            _ok = false;
        }
    }

    public bool Registered => _ok;

    public void Dispose()
    {
        if (_ok && _handle != IntPtr.Zero)
        {
            try { AvRevertMmThreadCharacteristics(_handle); } catch { /* 忽略 */ }
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
