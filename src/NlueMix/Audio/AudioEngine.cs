using NlueMix.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NlueMix.Audio;

/// <summary>
/// 音频引擎总控。
///
/// 数据流：
///   本机所有声音 ──WASAPI 环回──┐
///                               ├─→ 混音器 ─→ 输出环 ─→ 蓝牙音响（WASAPI 共享模式）
///   手机 1/2/3 ──网络──解码──────┘
///
/// 关键点：引擎唯一控制的出口就是"音响"这个端点，所以音质损失只有蓝牙这一次编码。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int MaxPhones = 3;
    private const int ChunkFrames = 480; // 10 ms

    private readonly AppSettings _settings;
    private readonly object _gate = new();

    private AudioRing? _systemRing;
    private AudioRing? _outputRing;

    /// <summary>
    /// 手机音频缓冲。必须在构造时就固定下来并一直复用：
    /// 网络接收端（写入）和混音器（读取）必须握的是同一批对象。
    /// </summary>
    private readonly AudioRing[] _phoneRings = Enumerable.Range(0, MaxPhones)
        .Select(_ => new AudioRing(SampleRate * 2, Channels))
        .ToArray();
    private RingSampleProvider? _outputProvider;
    private Mixer? _mixer;
    private SystemSource? _systemSource;
    private WasapiOut? _output;
    private MMDeviceEnumerator? _deviceWatcher;
    private DeviceNotification? _deviceNotification;
    private SessionDucker? _ducker;
    private string? _currentDeviceId;

    public AudioEngine(AppSettings settings) => _settings = settings;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string OutputDeviceName { get; private set; } = "-";
    public string OutputFormat { get; private set; } = "-";
    public string CaptureDeviceName => _systemSource?.DeviceName ?? "-";
    public string CaptureFormat => _systemSource?.CaptureFormat ?? "-";
    // 输出环已经不参与音频通路了，这里如实报告 WASAPI 的实际缓冲时延
    public int OutputBufferLatencyMs => OutputLatencyMs;
    public int OutputLatencyMs { get; private set; }
    public int PhoneSlotCount => MaxPhones;
    public Mixer? Mixer => _mixer;
    public AudioRing PhoneRing(int index) => _phoneRings[index];
    public AudioRing[] PhoneRings => _phoneRings;
    public SystemSource? SystemSource => _systemSource;
    public string? CurrentDeviceId => _currentDeviceId;

    /// <summary>
    /// 电脑声音的"让路"控制器。
    ///
    /// 原理：枚举输出设备上所有音频会话（除了 NlueMix 自己），
    /// 直接设置它们的音量。这样电脑声音完全没被"录进来再放出去"，
    /// 从根上避免了回音。
    /// </summary>
    private sealed class SessionDucker
    {
        private readonly NAudio.CoreAudioApi.MMDeviceEnumerator _enum = new();
        private readonly string _deviceId;
        private readonly System.Threading.Timer _fade;
        private readonly Dictionary<uint, float> _original = new();
        private float _current = 1f;
        private volatile float _target = 1f;

        /// <summary>当前活着的那个 ducker —— 进程退出时最后兜底恢复一次</summary>
        private static SessionDucker? _active;

        static SessionDucker()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { _active?.Restore(); } catch { }
                };
            }
            catch { }
        }

        public SessionDucker(string deviceId)
        {
            _deviceId = deviceId;
            _active = this;
            _fade = new System.Threading.Timer(_ => Tick(), null, 40, 40);
        }

        public float Current => _current;

        public void SetTarget(float value) => _target = Math.Clamp(value, 0f, 1f);

        /// <summary>
        /// 恢复：把所有被动过的程序还原成它们【原本】的音量。
        /// ★ 修复：旧代码统一写 1.0，会把用户原本调到 30% 的程序强行拉到 100%。
        /// </summary>
        public void Restore()
        {
            _target = 1f;
            _current = 1f;
            try
            {
                using var device = _enum.GetDevice(_deviceId);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        if (_original.TryGetValue(session.GetProcessID, out float original))
                            session.SimpleAudioVolume.Volume = original;
                    }
                    catch { }
                    finally { try { session.Dispose(); } catch { } }
                }
            }
            catch { }
            _original.Clear();
        }

        private void Tick()
        {
            try
            {
                // 每 40 ms 走一小步，约 0.4 秒走完全程
                float step = 0.1f;
                if (Math.Abs(_current - _target) < step)
                {
                    if (Math.Abs(_current - _target) < 0.001f) return;
                    _current = _target;
                }
                else
                {
                    _current += _current < _target ? step : -step;
                }
                Apply(_current);
            }
            catch { }
        }

        private void Apply(float gain)
        {
            try
            {
                using var device = _enum.GetDevice(_deviceId);
                var sessions = device.AudioSessionManager.Sessions;
                int myPid = Environment.ProcessId;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        uint pid = session.GetProcessID;
                        if (pid == (uint)myPid) continue;   // 不压自己

                        // ★ 第一次碰到某个程序时先记下它当前的音量，
                        //   之后按"原音量 × 让路系数"来调，绝不覆盖用户自己的设置。
                        if (!_original.TryGetValue(pid, out float original))
                        {
                            original = session.SimpleAudioVolume.Volume;
                            _original[pid] = original;
                        }
                        session.SimpleAudioVolume.Volume = original * gain;
                    }
                    catch { }
                    finally { try { session.Dispose(); } catch { } }   // 不释放会每秒漏 25 个 COM 对象
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try { if (ReferenceEquals(_active, this)) _active = null; } catch { }
            try { _fade.Dispose(); } catch { }
            try { _enum.Dispose(); } catch { }
        }
    }

    /// <summary>输出设备热插拔 / 掉线 / 上线。</summary>
    public event EventHandler<string>? OutputDeviceChanged;
    public event EventHandler<bool>? YieldingChanged;
    public event EventHandler<EngineState>? StateChanged;

    public bool IsRunning => State == EngineState.Running || State == EngineState.Yielding;

    // ---------------------------------------------------------------- 启动

    public void Start(string? renderDeviceId, int targetBufferMs = 60, bool startCapture = true)
    {
        lock (_gate)
        {
            Stop();

            int ringFrames = Math.Max(SampleRate / 2, targetBufferMs * SampleRate / 1000);
            _outputRing = new AudioRing(ringFrames, Channels);
            // 手机环在字段初始化时就建好了，这里只清空、不换对象
            foreach (var ring in _phoneRings) ring.Clear();

            _mixer = new Mixer(_phoneRings, SampleRate, ChunkFrames)
            {
                Mode = _settings.DuckMode,
                DuckLevel = _settings.DuckLevel,
            };
            _mixer.SetMasterVolume(_settings.MasterVolume);
            for (int i = 0; i < MaxPhones; i++) _mixer.SetPhoneVolume(i, 1f);
            _mixer.YieldingChanged += (_, yielding) =>
            {
                // 让路 = 把电脑上其他程序的音量压下去/静默（不是把声音录进来再放）
                _ducker?.SetTarget(_mixer.DuckMultiplier);
                State = yielding ? EngineState.Yielding : EngineState.Running;
                try { YieldingChanged?.Invoke(this, yielding); } catch { }
                try { StateChanged?.Invoke(this, State); } catch { }
            };

            _outputProvider = new RingSampleProvider(_outputRing, SampleRate, Channels);

            var device = AudioDevices.Resolve(renderDeviceId) ?? AudioDevices.DefaultRenderDevice();
            if (device is null)
            {
                State = EngineState.Faulted;
                throw new InvalidOperationException("没有找到可用的播放设备。请先连接蓝牙音响。");
            }

            _currentDeviceId = device.ID;
            OutputDeviceName = device.FriendlyName;

            int deviceRate = SampleRate;
            try { deviceRate = device.AudioClient.MixFormat.SampleRate; } catch { }
            OutputFormat = Dsp.DescribeRate(deviceRate) + " · " + Channels + " ch · float32"
                + (deviceRate == SampleRate ? "" : "（设备原生 " + Dsp.DescribeRate(deviceRate) + "，已插值）");

            // ★ 修复：原来这里是 _mixer.AttachDeviceResampler(_outputProvider, deviceRate)，
            //   而 AttachDeviceResampler 内部把 Mixer.Read 整个短路成"读输出环"，
            //   可是那个环只在 RenderChunk 里写、而 RenderChunk 又永远不会被调用 →
            //   设备采样率不是 48 kHz 时，音箱收到的是一片数字静音。
            //   正确做法：让重采样包在混音器【外面】，混音器照常渲染。
            ISampleProvider outputSource = _mixer;
            if (deviceRate != SampleRate)
            {
                outputSource = new WdlResamplingSampleProvider(_mixer, deviceRate);
                Log.Info("[引擎] 输出设备采样率是 " + deviceRate + " Hz，已在混音器外面加一级重采样");
            }

            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 45);
            _output = output;
            output.Init(outputSource);
            output.Play();
            OutputLatencyMs = 45;

            Log.Info("[引擎] WasapiOut 已启动：状态=" + output.PlaybackState
                + "，设备=" + device.FriendlyName
                + "，格式=" + (output.OutputWaveFormat?.ToString() ?? "?")
                + "，系统环容量=" + (_systemRing?.CapacityFrames ?? 0) + " 帧");

            // 关键：不再把"电脑自己的声音"抓回来播放（那是回音的根源）。
            // 电脑音频由 Windows 正常播放，NlueMix 只往同一个设备里叠加手机声音。
            _ducker?.Dispose();
            _ducker = new SessionDucker(device.ID);
            Log.Info("电脑声音让路方式：按音频会话控制（不会产生回音）");

            State = EngineState.Running;
            try { StateChanged?.Invoke(this, State); } catch { }
            Log.Info("音频引擎已启动 → " + OutputDeviceName + " / " + OutputFormat);
        }
    }

    public void StartCapture(string? captureDeviceId)
    {
        try
        {
            _systemSource?.Dispose();
            _systemSource = new SystemSource(_systemRing!, SampleRate);
            // 默认抓"默认播放设备"；如果用户指定了设备就用指定的
            _systemSource.Start(captureDeviceId);
        }
        catch (Exception ex)
        {
            Log.Error("启动本机声音采集失败: " + ex.Message);
            throw;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;

            try { _systemSource?.Dispose(); } catch { }
            _systemSource = null;

            try { _ducker?.Restore(); _ducker?.Dispose(); } catch { }
            _ducker = null;

            try { _mixer?.Dispose(); } catch { }
            _mixer = null;

            _outputProvider = null;
            _outputRing = null;
            _systemRing = null;
            foreach (var ring in _phoneRings) ring.Clear();

            State = EngineState.Stopped;
            try { StateChanged?.Invoke(this, State); } catch { }
        }
    }

    // ---------------------------------------------------------------- 运行参数

    public void SetMasterVolume(float volume) { _mixer?.SetMasterVolume(volume); _settings.MasterVolume = volume; }
    public void SetPhoneVolume(int index, float volume) => _mixer?.SetPhoneVolume(index, volume);
    public float GetPhoneGain(int index) => _mixer?.GetPhoneGain(index) ?? 0f;

    /// <summary>把某一路手机的检测器接到混音器上（必须在 Start 之后调用）。</summary>
    public void AttachPhoneDetector(int index, SilenceDetector detector)
        => _mixer?.SetPhoneDetector(index, detector);

    public DuckMode DuckMode
    {
        get => _mixer?.Mode ?? _settings.DuckMode;
        set
        {
            _settings.DuckMode = value;
            if (_mixer is not null) _mixer.Mode = value;
        }
    }

    public bool ManualYield
    {
        get => _mixer?.ManualYield ?? false;
        set { if (_mixer is not null) _mixer.ManualYield = value; }
    }

    /// <summary>从当前状态算一份统计快照给界面。</summary>
    public EngineStats Snapshot()
    {
        var stats = new EngineStats
        {
            State = State,
            OutputDevice = OutputDeviceName,
            OutputFormat = OutputFormat,
            OutputLatencyMs = OutputLatencyMs,
            BufferLatencyMs = OutputBufferLatencyMs,
        };
        if (_outputProvider is not null)
        {
            stats.UnderrunFrames = _outputProvider.NewUnderruns;
            stats.DroppedFrames = _outputProvider.NewOverflows;
            _outputProvider.SnapshotCounters();
        }
        return stats;
    }

    // ---------------------------------------------------------------- 设备变化监视

    public void WatchDevices()
    {
        try
        {
            _deviceWatcher = new MMDeviceEnumerator();
            // ★ 必须抓住这个实例，否则注销时传 null，回调在 Dispose 之后还会被触发
            _deviceNotification = new DeviceNotification(this);
            _deviceWatcher.RegisterEndpointNotificationCallback(_deviceNotification);
        }
        catch (Exception ex)
        {
            Log.Warn("注册设备变化通知失败（不影响使用）: " + ex.Message);
        }
    }

    private void OnEndpointChanged()
    {
        try
        {
            var device = AudioDevices.Resolve(_currentDeviceId);
            if (device is null || device.State != DeviceState.Active)
            {
                Log.Warn("输出设备已不可用: " + (OutputDeviceName));
                OutputDeviceChanged?.Invoke(this, "lost");
            }
            else
            {
                OutputDeviceChanged?.Invoke(this, "ok");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("处理设备变化失败: " + ex.Message);
        }
    }

    private sealed class DeviceNotification : IMMNotificationClient
    {
        private readonly AudioEngine _engine;
        public DeviceNotification(AudioEngine engine) => _engine = engine;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _engine.OnEndpointChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => _engine.OnEndpointChanged();
        public void OnDeviceRemoved(string deviceId) => _engine.OnEndpointChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public void Dispose()
    {
        try { if (_deviceNotification is not null) _deviceWatcher?.UnregisterEndpointNotificationCallback(_deviceNotification); } catch { }
        try { _deviceWatcher?.Dispose(); } catch { }
        Stop();
    }
}
