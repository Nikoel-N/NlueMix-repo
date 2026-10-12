namespace NlueMix.Core;

/// <summary>蓝牙音响（输出目标）。</summary>
public sealed class SpeakerInfo
{
    public string Name { get; set; } = "";
    public string? Mac { get; set; }
    public string? DeviceId { get; set; }
    public bool IsConnected { get; set; }
    public bool IsPaired { get; set; }
    public int? BatteryPercent { get; set; }
    public string? AudioEndpointName { get; set; }
    public bool IsAudioEndpointPresent { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Loc.T("speaker.unknown") : Name;
    public string StatusText => IsConnected ? Loc.T("speaker.connected")
                             : IsPaired    ? Loc.T("speaker.pairednc")
                                           : Loc.T("speaker.unpaired");
    public override string ToString() => DisplayName + " (" + (Mac ?? "无地址") + ")";
}

/// <summary>一路音频来源（本机 / 某台手机）。</summary>
public sealed class AudioSourceInfo
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "phone"; // system | phone
    public bool IsOnline { get; set; }
    public bool IsActive { get; set; }
    public double LevelDb { get; set; } = -96;
    public double PeakDb { get; set; } = -96;
    public long PacketsReceived { get; set; }
    public long PacketsLost { get; set; }
    public string? RemoteAddress { get; set; }
    public string Codec { get; set; } = "-";
    public int SampleRate { get; set; }
}

/// <summary>手机让路策略。</summary>
public enum DuckMode
{
    /// <summary>手机出声 → 电脑全部淡出静音；手机停 → 自动恢复（默认）</summary>
    AutoSilence = 0,
    /// <summary>手机出声 → 电脑音量压低到 20%</summary>
    AutoDuck = 1,
    /// <summary>不分主次，一起放</summary>
    Mix = 2,
    /// <summary>只手动切</summary>
    Manual = 3,
}

public enum EngineState
{
    Stopped = 0,
    Running = 1,
    /// <summary>正在为手机让路</summary>
    Yielding = 2,
    Faulted = 3,
}

/// <summary>运行期统计，界面每秒刷新。</summary>
public sealed class EngineStats
{
    public EngineState State { get; set; }
    public string OutputDevice { get; set; } = "-";
    public string OutputFormat { get; set; } = "-";
    public int OutputLatencyMs { get; set; }
    public int BufferLatencyMs { get; set; }
    public double CpuPercent { get; set; }
    public long DroppedFrames { get; set; }
    public long UnderrunFrames { get; set; }
}

/// <summary>持久化设置。</summary>
public sealed class AppSettings
{
    public string? SpeakerMac { get; set; }
    public string? SpeakerName { get; set; }
    public string? RenderDeviceId { get; set; }
    public float MasterVolume { get; set; } = 0.85f;
    public float SystemVolume { get; set; } = 1.0f;
    public DuckMode DuckMode { get; set; } = DuckMode.AutoSilence;

    /// <summary>界面主题：0=深色，1=浅色。默认深色。</summary>
    public int Theme { get; set; } = 0;

    /// <summary>界面语言代码，如 "zh-CN" / "en"。默认简体中文。</summary>
    public string Language { get; set; } = "zh-CN";
    public float DuckLevel { get; set; } = 0.2f;
    public string PairCode { get; set; } = "0000";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int Port { get; set; } = 47820;
    /// <summary>手机的局域网 IP（无线采集用；留空则自动用 mDNS 找）</summary>
    public string PhoneIp { get; set; } = "";
    public int TargetBufferMs { get; set; } = 60;
    public List<string> TrustedPhones { get; set; } = new();
    public List<string> RecentSpeakers { get; set; } = new();

    /// <summary>
    /// 用户点过「同意，并永久关闭此提示」。
    ///
    /// 注意语义：这个标志为 true 表示用户在**看过并勾选确认**之后主动选择不再弹窗，
    /// 也就是"永久同意"。它绝不能被程序自己静默置 true —— 那样就等于
    /// 在用户没看的情况下跳过了信息披露。
    ///
    /// 用户可以在「设置 → 关于 → 重新显示风险提示」里把它改回 false，
    /// 这是《个人信息保护法》第 15 条要求的撤回同意权。
    /// </summary>
    public bool SkipRiskNotice { get; set; }

    /// <summary>同意时的版本号。文案大改后可以提高版本号，让所有用户重新确认一次。</summary>
    public string? RiskNoticeAcceptedVersion { get; set; }
}
