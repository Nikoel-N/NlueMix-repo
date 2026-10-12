using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NlueMix.Audio;
using NlueMix.Bluetooth;
using NlueMix.Core;
using NlueMix.Network;

namespace NlueMix.UI;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AudioEngine _engine;
    private readonly NetworkReceiver _receiver;
    private readonly ObservableCollection<SourceRow> _rows = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _speakerTimer;

    private IReadOnlyList<SpeakerInfo> _speakers = Array.Empty<SpeakerInfo>();
    private SpeakerInfo? _currentSpeaker;
    private bool _suppressUiEvents;
    private bool _masterMuted;
    private bool _starting;
    private volatile bool _scanning;

    public MainWindow()
    {
        Log.Info("MainWindow ctor: 开始");
        try
        {
            InitializeComponent();
            Log.Info("MainWindow ctor: XAML 已加载");
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow ctor: XAML 加载失败 " + ex);
            throw;
        }

        _settings = SettingsStore.Load();
        Log.Info("MainWindow ctor: 设置已读取，连接码 " + _settings.PairCode);
        _engine = new AudioEngine(_settings);
        _receiver = new NetworkReceiver(
            _settings.Port,
            _settings.Port + 1,
            _settings.PairCode,
            // 关键：手机音频缓冲必须【和混音器用同一批对象】。
            // 之前这里自己 new 了一组，结果手机声音写进了没人读的缓冲区，永远听不到。
            _engine.PhoneRings,
            _ => { });

        for (int i = 0; i < AudioEngine.MaxPhones; i++)
        {
            var row = new SourceRow(i, _receiver.Slots[i]);
            row.VolumeChanged += OnPhoneVolumeChanged;
            _rows.Add(row);

            // 关键：把会话的检测器接到混音器，让"让路"判定读到真实的音频电平
            _engine.AttachPhoneDetector(i, _receiver.Slots[i].Detector);
        }
        SourceList.ItemsSource = _rows;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(120) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        _speakerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _speakerTimer.Tick += (_, _) => RefreshSpeakers();
        _speakerTimer.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Log.Info("MainWindow ctor: 完成");
    }

    // ────────────────────────────────────────────── 启动

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Info("MainWindow: Loaded 事件触发");
        try
        {
            Log.Info("MainWindow: 开始初始化界面状态");
            _suppressUiEvents = true;
            MasterSlider.Value = Math.Round(_settings.MasterVolume * 100);
            TxtMasterValue.Text = (int)Math.Round(_settings.MasterVolume * 100) + "%";
            ModeCombo.SelectedIndex = (int)_settings.DuckMode;
            _suppressUiEvents = false;

            Log.Info("MainWindow: 界面状态就绪，准备异步扫描音响");
            StartSpeakerScan();
            Log.Info("MainWindow: 已派发扫描任务（不阻塞启动）");

            _engine.YieldingChanged += (_, yielding) => Dispatcher.BeginInvoke(() => UpdateYieldHint(yielding));
            _engine.WatchDevices();
            _engine.OutputDeviceChanged += (_, state) => Dispatcher.BeginInvoke(() => OnOutputDeviceChanged(state));

            _receiver.SessionChanged += (_, _) => Dispatcher.BeginInvoke(RefreshSourceRows);
            _receiver.Start();
            Log.Info("MainWindow: 手机接收端已启动");

            await StartEngineAsync();
            SettingsStore.Save(_settings);
            // 启动时静默检查一次防火墙放行情况。
        // 只记日志、只在有问题时记 WARN —— 不在启动时弹 UAC，那太打扰。
        // 用户遇到"手机连不上"时会去点工具栏的 🛡。
        try
        {
            var profiles = Core.FirewallFix.GetRuleProfiles();
            if (Core.FirewallFix.IsFullyOpen())
            {
                Log.Info("[防火墙] 规则「" + Core.FirewallFix.LegacyRule + "」覆盖：" + profiles);
            }
            else
            {
                Log.Warn("[防火墙] 规则「" + Core.FirewallFix.LegacyRule + "」只覆盖「" + profiles + "」，" +
                         "如果网卡网络配置不是这个，手机将连不上电脑。" +
                         "点工具栏 🛡 可以修复。手工命令：" + Core.FirewallFix.ManualCommand);
            }
        }
        catch (Exception ex) { Log.Warn("[防火墙] 启动检查失败: " + ex.Message); }

        // 主题按钮的图标要和【当前实际主题】一致。
        // 不设的话它永远是 XAML 里写死的那个，用户会看到"月亮图标但界面是浅色"这种矛盾。
        BtnTheme.Content = ThemeManager.Current == ThemeManager.Mode.Light ? "☀" : "🌙";
        ApplyLanguage();

        Log.Info("MainWindow: 初始化全部完成");
        }
        catch (Exception ex)
        {
            Log.Error("启动失败: " + ex);
            MessageBox.Show("启动时出了点问题：\n\n" + ex.Message + "\n\n可以点右上角的刷新重试。",
                "NlueMix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StartEngineAsync()
    {
        if (_starting) return;
        _starting = true;
        try
        {
            var speaker = await Task.Run(() => ResolveSpeaker());
            _currentSpeaker = speaker;

            string? deviceId = _settings.RenderDeviceId;
            if (string.IsNullOrWhiteSpace(deviceId) && speaker?.DeviceId is not null)
                deviceId = speaker.DeviceId;

            await Task.Run(() => _engine.Start(deviceId, _settings.TargetBufferMs));

            // Start() 会新建混音器，所以这里必须重新挂接检测器
            for (int i = 0; i < AudioEngine.MaxPhones; i++)
                _engine.AttachPhoneDetector(i, _receiver.Slots[i].Detector);
            Log.Info("MainWindow: 已把 3 路手机的检测器挂到混音器");

            ShowConnectionInfo();
        }
        catch (Exception ex)
        {
            Log.Error("音频引擎启动失败: " + ex.Message);
            TxtEngine.Text = Core.Loc.T("status.engineoff") + " · " + ex.Message;
            DotEngine.Fill = (Brush)FindResource("Bad");
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// 在界面上显示"电脑地址 + 连接码"。
    ///
    /// 电脑可能同时有多个网络（有线 + 无线 + 手机热点），
    /// 所以这里把**所有可用地址**都列出来，主地址选最可能被手机访问到的那个。
    /// </summary>
    private void ShowConnectionInfo()
    {
        try
        {
            var all = NetworkReceiver.ListLocalAddresses();
            var ranked = all
                .Select(ip => new { Ip = ip, Score = ScoreAddress(ip) })
                .OrderByDescending(x => x.Score)
                .ToList();

            if (ranked.Count == 0)
            {
                TxtIp.Text = Core.Loc.T("howto.nonet");
                TxtConnectHint.Text = Core.Loc.T("howto.noaddr");
                OtherIpList.ItemsSource = null;
            }
            else
            {
                TxtIp.Text = ranked[0].Ip;

                // 其余的地址也列出来，连不上时可以试另一个
                var others = ranked.Skip(1)
                    .Select(x => new IpRow { Address = x.Ip, Hint = DescribeLink(x.Ip) })
                    .ToList();
                OtherIpList.ItemsSource = others;

                TxtConnectHint.Text = others.Count > 0
                    ? Core.Loc.T("howto.hint")
                    : Core.Loc.T("howto.hint2");
            }

            TxtCode.Text = _settings.PairCode;
            Log.Info("连接信息：" + (ranked.Count > 0 ? ranked[0].Ip : "无地址")
                + "，连接码 " + _settings.PairCode
                + "，全部地址 [" + string.Join(", ", all) + "]");
        }
        catch (Exception ex)
        {
            Log.Warn("显示连接信息失败: " + ex.Message);
            TxtIp.Text = Core.Loc.T("howto.detectfail");
            TxtCode.Text = _settings.PairCode;
        }
    }

    /// <summary>界面上"其他地址"的一行</summary>
    public sealed class IpRow
    {
        public string Address { get; set; } = "";
        public string Hint { get; set; } = "";
    }

    /// <summary>
    /// 给地址打分，分高的优先显示。
    /// 家用路由器（192.168.x.x）最常见，其次是公司网络（10.x），再是手机热点。
    /// </summary>
    private static int ScoreAddress(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return -1;
        if (!int.TryParse(parts[0], out int a)) return -1;
        if (!int.TryParse(parts[1], out int b)) return -1;

        // 手机热点常见网段：安卓 192.168.43.x / 192.168.42.x，iPhone 172.20.10.x
        if (a == 192 && (b == 168)) return 100;
        if (a == 172 && b == 20) return 95;
        if (a == 10) return 90;
        if (a == 172 && b >= 16 && b <= 31) return 85;
        if (a == 169 && b == 254) return 10;   // 自动私网地址：通常是没连上
        if (a == 26 || a == 25 || a == 100) return 5;  // 虚拟网卡 / 运营商内网
        return 50;
    }

    /// <summary>给地址加个人话说明</summary>
    private static string DescribeLink(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return "";
        if (!int.TryParse(parts[0], out int a)) return "";
        if (!int.TryParse(parts[1], out int b)) return "";

        if (a == 192 && b == 168) return Core.Loc.T("link.home");
        if (a == 172 && b == 20) return Core.Loc.T("link.iphone");
        if (a == 10) return Core.Loc.T("link.corp");
        if (a == 172 && b >= 16 && b <= 31) return Core.Loc.T("link.intranet");
        if (a == 169 && b == 254) return Core.Loc.T("link.nonet");
        if (a == 26 || a == 25 || a == 100) return Core.Loc.T("link.virtual");
        return "";
    }

    private void OnCopyIp(object sender, RoutedEventArgs e) => CopyToClipboard(TxtIp.Text, "howto.pcaddr");
    private void OnCopyCode(object sender, RoutedEventArgs e) => CopyToClipboard(TxtCode.Text, "howto.code");

    /// <summary>
    /// 复制到剪贴板，并在提示行里回显。
    ///
    /// 注意这里的守卫：原先写的是「文本里含『检测』两个字就不复制」。
    /// 界面支持八国语言之后那种判断就不成立了 —— 英文界面下 TxtIp 显示的是
    /// "Detection failed"，一个中文字都没有，于是会把一句错误提示当地址复制走。
    /// 所以改成按【内容形态】判断：要么是合法 IP，要么是 4 位连接码。
    /// </summary>
    private void CopyToClipboard(string? text, string whatKey)
    {
        var value = text?.Trim();
        if (string.IsNullOrWhiteSpace(value) || !LooksCopyable(value)) return;

        try
        {
            System.Windows.Clipboard.SetText(value);
            Log.Info("已复制：" + value);
            TxtConnectHint.Text = "✅ " + Core.Loc.T("howto.copied") + " "
                                  + Core.Loc.T(whatKey) + "：" + value;
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败: " + ex.Message);
        }
    }

    /// <summary>只有像 IP 或连接码的内容才允许复制，免得把提示语当成值复制走。</summary>
    private static bool LooksCopyable(string s)
    {
        if (System.Net.IPAddress.TryParse(s, out _)) return true;
        if (s.Length != 4) return false;
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return true;
    }

    private void OnCopyOtherIp(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string ip }) CopyToClipboard(ip, "howto.pcaddr");
    }
    /// <summary>挑一台最像"当前在用的蓝牙音响"的设备。</summary>
    private SpeakerInfo? ResolveSpeaker()
    {
        var speakers = _speakers.Count > 0
            ? _speakers
            : Task.Run(() => BluetoothScanner.Scan()).GetAwaiter().GetResult();
        if (speakers.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(_settings.SpeakerMac))
        {
            var remembered = speakers.FirstOrDefault(s => s.Mac == _settings.SpeakerMac && s.IsConnected);
            if (remembered is not null) return remembered;
        }

        return speakers.FirstOrDefault(s => s.IsConnected) ?? speakers.FirstOrDefault();
    }

    // ────────────────────────────────────────────── 音响

    /// <summary>
    /// 异步扫描蓝牙音响。做成"发射后不管"：
    /// 界面先出来，扫描在后台慢慢做，做完再回主线程更新。
    /// 这样即使扫描卡住，界面也是活的。
    /// </summary>
    private void StartSpeakerScan()
    {
        if (_scanning) return;
        _scanning = true;

        _ = Task.Run(() =>
        {
            IReadOnlyList<SpeakerInfo> list;
            try
            {
                Log.Info("扫描线程: 开始");
                list = BluetoothScanner.Scan();
                Log.Info("扫描线程: 完成，找到 " + list.Count + " 个设备");
            }
            catch (Exception ex)
            {
                Log.Error("扫描蓝牙音响失败: " + ex);
                list = Array.Empty<SpeakerInfo>();
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _speakers = list;
                        SpeakerList.ItemsSource = _speakers;
                        UpdateSpeakerCard();
                        Log.Info("界面: 音响列表已更新");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("更新音响列表界面失败: " + ex.Message);
                    }
                    finally
                    {
                        _scanning = false;
                    }
                }));
            }
            catch (Exception ex)
            {
                Log.Warn("回主线程更新音响列表失败: " + ex.Message);
                _scanning = false;
            }
        });
    }

    private void RefreshSpeakers() => StartSpeakerScan();

    private void UpdateSpeakerCard()
    {
        var speaker = _currentSpeaker ?? ResolveSpeaker();
        var active = _speakers.FirstOrDefault(s => s.IsConnected && (s.Mac == speaker?.Mac || s.IsConnected));
        if (active is not null) speaker = active;

        if (speaker is null)
        {
            TxtSpeakerName.Text = Core.Loc.T("speaker.nopair");
            TxtSpeakerStatus.Text = Core.Loc.T("speaker.nopairhint");
            TxtSpeakerDetail.Text = "";
            DotSpeaker.Fill = (Brush)FindResource("Bad");
            BtnConnect.Content = Core.Loc.T("speaker.gopair");
            return;
        }

        _currentSpeaker = speaker;
        TxtSpeakerName.Text = speaker.DisplayName;
        TxtSpeakerStatus.Text = speaker.StatusText;
        DotSpeaker.Fill = (Brush)FindResource(speaker.IsConnected ? "Good" : speaker.IsPaired ? "Warn" : "Bad");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(speaker.Mac)) parts.Add(speaker.Mac);
        if (speaker.IsConnected && !string.IsNullOrWhiteSpace(speaker.AudioEndpointName)) parts.Add(speaker.AudioEndpointName);
        if (speaker.BatteryPercent is int battery) parts.Add(Core.Loc.T("speaker.battery", battery));
        TxtSpeakerDetail.Text = string.Join("  ·  ", parts);

        BtnConnect.Content = speaker.IsConnected ? Core.Loc.T("speaker.connected")
                           : speaker.IsPaired    ? Core.Loc.T("speaker.connect")
                                                 : Core.Loc.T("speaker.gopair");
        BtnConnect.IsEnabled = !speaker.IsConnected;
    }

    private async void OnConnectSpeaker(object sender, RoutedEventArgs e)
    {
        var speaker = _currentSpeaker;
        if (speaker is null || string.IsNullOrWhiteSpace(speaker.Mac))
        {
            BluetoothConnector.OpenWindowsBluetoothSettings();
            return;
        }

        if (!speaker.IsPaired)
        {
            BluetoothConnector.OpenWindowsBluetoothSettings();
            return;
        }

        BtnConnect.Content = Core.Loc.T("speaker.connecting");
        BtnConnect.IsEnabled = false;
        try
        {
            bool ok = await BluetoothConnector.ConnectAsync(speaker.Mac);
            await Task.Delay(1200);
            RefreshSpeakers();

            if (!ok)
            {
                MessageBox.Show(
                    "没能自动连上这台音响。\n\n" +
                    "请到「设置 → 蓝牙和其他设备」里点一下它，或者把音响重新开机再试。\n" +
                    "NlueMix 已经打开了系统蓝牙设置页，配对一次以后就不用再配了。",
                    "NlueMix", MessageBoxButton.OK, MessageBoxImage.Information);
                BluetoothConnector.OpenWindowsBluetoothSettings();
            }

            await StartEngineAsync();
        }
        catch (Exception ex)
        {
            Log.Error("连接音响失败: " + ex.Message);
        }
        finally
        {
            UpdateSpeakerCard();
        }
    }

    private void OnPickSpeaker(object sender, RoutedEventArgs e)
    {
        RefreshSpeakers();
        SpeakerPopup.IsOpen = true;

        // Popup 里的内容【不在主窗口的可视化树上】—— 它自己是一棵独立的树，
        // 而且只有打开之后才建起来。所以 ApplyLanguage() 那一次遍历够不着它，
        // 必须在打开之后再补一遍，否则这个下拉里的字永远是上一次语言。
        if (SpeakerPopup.Child is System.Windows.DependencyObject popupRoot)
            ApplyLanguageTo(popupRoot);
    }

    private async void OnSpeakerPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpeakerInfo speaker }) return;
        SpeakerPopup.IsOpen = false;

        _settings.SpeakerMac = speaker.Mac;
        _settings.SpeakerName = speaker.Name;
        if (speaker.DeviceId is not null) _settings.RenderDeviceId = speaker.DeviceId;
        SettingsStore.Save(_settings);

        _currentSpeaker = speaker;
        UpdateSpeakerCard();

        if (speaker.IsConnected) await StartEngineAsync();
        else OnConnectSpeaker(sender, e);
    }

    private void OnOutputDeviceChanged(string state)
    {
        UpdateSpeakerCard();
        if (state == "lost")
        {
            TxtEngine.Text = Core.Loc.T("speaker.lost");
            DotEngine.Fill = (Brush)FindResource("Warn");
        }
        else
        {
            _ = StartEngineAsync();
        }
    }

    /// <summary>
    /// USB（有线）采集：以 shell 身份运行采集程序，48 kHz 立体声 PCM16 无损。
    ///
    /// 手机端 App 走 MediaProjection 采不到声音的机型（如 Redmi K70 Ultra / HyperOS）用这个，
    /// 插上数据线、点这里开始，再点停止。不需要录屏授权，手机端也不需要装 App。
    /// </summary>
    private void OnUsbCaptureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_receiver.UsbCaptureActive)
            {
                _receiver.StopUsbCapture();
                BtnUsb.Content = "🔌  采集手机声音";
                return;
            }

            if (_receiver.StartUsbCapture(string.IsNullOrWhiteSpace(_settings.PhoneIp) ? null : _settings.PhoneIp))
            {
                BtnUsb.Content = "⏹  停止采集";
                return;
            }

            MessageBox.Show(
                _receiver.UsbCaptureStatus.Length > 0 ? _receiver.UsbCaptureStatus : "手机采集启动失败。",
                "NlueMix · 手机采集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("手机采集操作失败: " + ex.Message);
            MessageBox.Show(ex.Message, "NlueMix · 手机采集", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshSpeakers();
        await StartEngineAsync();
    }

    private void OnOpenBluetoothSettings(object sender, RoutedEventArgs e)
        => BluetoothConnector.OpenWindowsBluetoothSettings();

    // ────────────────────────────────────────────── 手机音源

    private void RefreshSourceRows()
    {
        foreach (var row in _rows) row.Refresh();
    }

    private void OnPhoneVolumeChanged(int slot, float volume)
    {
        _engine.SetPhoneVolume(slot, volume);
    }

    private void OnPhoneMuteToggled(int slot, bool muted)
    {
        var row = _rows.FirstOrDefault(r => r.Slot == slot);
        if (row is null) return;
        row.Muted = muted;
        _engine.SetPhoneVolume(slot, muted ? 0f : (float)(row.Volume / 100.0));
        row.Refresh();
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceRow row }) return;
        OnPhoneMuteToggled(row.Slot, !row.Muted);
    }

    // ────────────────────────────────────────────── 总音量 / 模式

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 注意：XAML 里给 Slider.Value 赋初值时就会触发这个事件，
        // 那时构造函数还没跑完，_engine / _settings 都还是 null —— 必须先挡住。
        if (_suppressUiEvents || _engine is null || _settings is null) return;
        float volume = (float)(e.NewValue / 100.0);
        _settings.MasterVolume = volume;
        TxtMasterValue.Text = (int)Math.Round(e.NewValue) + "%";
        if (_masterMuted && volume > 0) { _masterMuted = false; BtnMuteAll.Content = "🔊"; }
        _engine.SetMasterVolume(volume);
        SettingsStore.Save(_settings);
    }

    private void OnMuteAll(object sender, RoutedEventArgs e)
    {
        _masterMuted = !_masterMuted;
        if (_masterMuted)
        {
            _engine.SetMasterVolume(0f);
            BtnMuteAll.Content = "🔇";
        }
        else
        {
            _engine.SetMasterVolume((float)(MasterSlider.Value / 100.0));
            BtnMuteAll.Content = "🔊";
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _engine is null || _settings is null) return;
        var mode = (DuckMode)Math.Max(0, ModeCombo.SelectedIndex);
        _settings.DuckMode = mode;
        _engine.DuckMode = mode;
        BtnManualYield.IsEnabled = mode == DuckMode.Manual;
        SettingsStore.Save(_settings);
    }

    private void OnManualYield(object sender, RoutedEventArgs e)
    {
        _engine.ManualYield = !_engine.ManualYield;
        UpdateManualYieldButton();
    }

    /// <summary>手动让路按钮上的字取决于当前状态，所以单独抽出来，换语言时也要重刷。</summary>
    private void UpdateManualYieldButton()
    {
        if (_engine is null) return;
        BtnManualYield.Content = _engine.ManualYield
            ? Core.Loc.T("duck.restore")
            : Core.Loc.T("duck.manualbtn");
    }

    private void UpdateYieldHint(bool yielding)
    {
        TxtAdvanced.Text = yielding ? Core.Loc.T("status.yielding") : "";
    }

    // ────────────────────────────────────────────── 每秒刷新

    private void RefreshUi()
    {
        try
        {
            if (_engine is null || _rows.Count == 0) return;
            RefreshSourceRows();

            var stats = _engine.Snapshot();
            var speaker = _currentSpeaker;
            string speakerText = speaker?.IsConnected == true ? speaker.DisplayName : Core.Loc.T("speaker.nospeaker");

            string state = _engine.State switch
            {
                EngineState.Running => Core.Loc.T("status.running"),
                EngineState.Yielding => Core.Loc.T("status.runningyield"),
                EngineState.Faulted => Core.Loc.T("status.faulted"),
                _ => Core.Loc.T("status.engineoff"),
            };

            DotEngine.Fill = (Brush)FindResource(_engine.State switch
            {
                EngineState.Running => "Good",
                EngineState.Yielding => "Warn",
                EngineState.Faulted => "Bad",
                _ => "TextFaint",
            });

            TxtEngine.Text = state + " · " + speakerText;

            var details = new List<string>();
            if (_engine.IsRunning)
            {
                details.Add(Core.Loc.T("status.out") + " " + _engine.OutputFormat);
                details.Add(Core.Loc.T("status.buf") + " " + stats.BufferLatencyMs + " ms");
                if (stats.UnderrunFrames > 0) details.Add(Core.Loc.T("status.underrun") + " " + stats.UnderrunFrames);
            }
            TxtAdvanced.Text = string.Join("  ·  ", details);
        }
        catch (Exception ex)
        {
            Log.Warn("刷新界面失败: " + ex.Message);
        }
    }

    // ────────────────────────────────────────────── 退出

    /// <summary>
    /// 检查并修复 Windows 防火墙放行。
    ///
    /// 存在的意义（实测踩到的坑）：防火墙的「程序规则」按「程序路径 + 网络配置」匹配。
    /// 安装时建的规则只覆盖 Public，那会儿两个网卡恰好都是 Public 所以一直好用；
    /// 后来以太网被识别成 Private，规则不再匹配 → 手机入站被静默丢掉 →
    /// 手机端只显示「连接不上电脑」，电脑端日志里连一条连接尝试都没有。用户无从判断。
    /// </summary>
    private void OnFirewallClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var profiles = Core.FirewallFix.GetRuleProfiles();

            if (Core.FirewallFix.IsFullyOpen())
            {
                MessageBox.Show(this,
                    "防火墙规则「" + Core.FirewallFix.LegacyRule + "」已经覆盖所有网络配置：\n\n" +
                    "    配置文件：" + profiles + "\n\n" +
                    "所以手机连不上不是防火墙的问题。可以检查：\n" +
                    "· 手机和电脑是不是在同一个局域网\n" +
                    "· 手机上填的电脑地址对不对（当前是 " + string.Join(" / ", Network.NetworkReceiver.ListLocalAddresses()) + "）\n" +
                    "· 手机上的连接码和电脑上显示的是不是一致（当前 " + _settings.PairCode + "）",
                    "防火墙检查", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                "检查到防火墙可能挡住了手机。\n\n" +
                "规则「" + Core.FirewallFix.LegacyRule + "」当前的配置文件是：\n" +
                "    " + (profiles.Length == 0 ? "（查不到这条规则）" : profiles) + "\n\n" +
                "而你的网卡网络配置变了之后，规则就不再匹配，\n" +
                "手机的连接会被系统静默丢掉，电脑端看不到任何记录。\n\n" +
                "要现在修复吗？会弹出管理员授权框（只能由你确认）。\n\n" +
                "如果不方便提权，可以手工在管理员命令行里执行：\n" +
                Core.FirewallFix.ManualCommand,
                "防火墙可能挡住了手机", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            // 以管理员身份重启自己跑修复（见 FirewallFix.RunElevatedSelfFix 的注释）。
            // 修复的详细报告由那个提权副本自己弹窗显示，这里只处理"没起来"的情况。
            Log.Info("[防火墙] 准备提权重启自己跑 " + Core.FirewallFix.FixArg);
            var started = Core.FirewallFix.RunElevatedSelfFix(out var error);

            if (!started && error.Length > 0)
            {
                Log.Warn("[防火墙] 提权失败: " + error);

                var cancelled = error.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                                error.Contains("取消", StringComparison.Ordinal);

                MessageBox.Show(this,
                    cancelled
                        ? "你取消了管理员授权，防火墙没有改动。\n\n" +
                          "如果不方便提权，可以在「管理员」命令行里手工执行：\n\n" +
                          Core.FirewallFix.ManualCommand
                        : "提权执行失败：\n\n" + error + "\n\n" +
                          "可以手工在管理员命令行里执行：\n\n" + Core.FirewallFix.ManualCommand,
                    "没能自动修复", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[防火墙] 处理失败: " + ex.Message);
            MessageBox.Show(this, "处理失败：" + ex.Message, "NlueMix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 手动连接手机。
    ///
    /// 存在的意义：手机端无障碍自动读端口在部分 ROM 上会读不到，
    /// 这时候必须有一条"人来填两个数字"的退路，否则功能直接死掉。
    /// 配对只做一次，所以手动一次的成本可以接受。
    /// </summary>
    private void OnManualPairClick(object sender, RoutedEventArgs e)
    {
        Log.Info("[手动配对] 按钮被点击，准备打开窗口");
        try
        {
            var win = new ManualPairWindow { Owner = this };
            Log.Info("[手动配对] 窗口已创建，即将 ShowDialog");
            win.ShowDialog();
            Log.Info("[手动配对] 窗口已关闭");

            // 用户填的 IP 顺手记住，下次省得再输
            var ip = win.GetEnteredIp();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                _settings.PhoneIp = ip;
                SettingsStore.Save(_settings);
                Log.Info("[手动配对] 已记住手机 IP: " + ip);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[手动配对] 打开窗口失败: " + ex.Message);
        }
    }

    /// <summary>打开反馈窗口。邮箱和日志路径都在里面，用户点一下就能写信并附上日志。</summary>
    private void OnFeedback(object sender, RoutedEventArgs e)
    {
        try { new FeedbackWindow { Owner = this }.ShowDialog(); }
        catch (Exception ex) { Log.Warn("打开反馈窗口失败: " + ex.Message); }
    }

    /// <summary>选语言。选完立即重新套用界面文案，不用重启。</summary>
    private void OnPickLanguage(object sender, RoutedEventArgs e)
    {
        try
        {
            new LanguageWindow { Owner = this }.ShowDialog();

            // 选完就记住，下次启动直接用这个语言
            if (_settings.Language != Core.Loc.CurrentCode)
            {
                _settings.Language = Core.Loc.CurrentCode;
                SettingsStore.Save(_settings);
                Log.Info("界面语言切换为：" + Core.Loc.CurrentCode);
            }
        }
        catch (Exception ex) { Log.Warn("打开语言窗口失败: " + ex.Message); }
    }

    /// <summary>
    /// 把当前语言的文案套到界面上。
    ///
    /// 做法：给要翻译的控件打 Tag="key"，这里遍历可视化树，
    /// 凡是有 Tag 且能在词表里找到的，就把 Text/Content 换掉。
    /// 比起给每个控件起名字再逐条赋值，这样加一个控件只要多写一个 Tag。
    /// </summary>
    public void ApplyLanguage()
    {
        try
        {
            // 工具条：图标 + 名称，名称跟着语言变
            Title                 = "NlueMix " + Core.Loc.T("app.title");
            BtnRefresh.Content    = "⟳  " + Core.Loc.T("btn.rescan");
            BtnUsb.Content        = _receiver.UsbCaptureActive
                                        ? "⏹  " + Core.Loc.T("btn.stopcapture")
                                        : "🔌  " + Core.Loc.T("btn.capture");
            BtnFirewall.Content   = "🛡  " + Core.Loc.T("btn.firewall");
            BtnManualPair.Content = "🔗  " + Core.Loc.T("btn.pair");
            BtnSettings.Content   = "⚙  " + Core.Loc.T("btn.bluetooth");

            // 打了 Tag 的控件统一套一遍；提示走 LocTag.ToolTipKey。
            // （右上角那几个图标按钮的提示也在这遍里，不必单独写。）
            ApplyLanguageTo(this);

            // ★ 上面那一遍只认 XAML 里写死的文案。下面这些是【按运行状态算出来】的，
            //   必须在这里重算一次 —— 否则切完语言要等下一次扫描或每秒刷新才跟着变，
            //   用户会先看到一屏半中半英的界面，还以为没切成功。
            UpdateSpeakerCard();
            UpdateManualYieldButton();
            ShowConnectionInfo();
            RefreshSourceRows();
        }
        catch (Exception ex) { Log.Warn("套用语言失败: " + ex.Message); }
    }

    private static void ApplyLanguageTo(System.Windows.DependencyObject node)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is System.Windows.FrameworkElement fe)
            {
                // 提示文案：Tag 已经被正文占用了，所以另走一个附加属性。
                // 用附加属性的好处是能写在 XAML 里，包括 DataTemplate 内部
                // （那里的控件拿不到 x:Name，代码里没法指名道姓地赋 ToolTip）。
                var tipKey = LocTag.GetToolTipKey(fe);
                if (!string.IsNullOrEmpty(tipKey)) fe.ToolTip = Core.Loc.T(tipKey);

                if (fe.Tag is string key && key.Length > 0)
                {
                    var text = Core.Loc.T(key);
                    if (fe is System.Windows.Controls.TextBlock tb) tb.Text = text;
                    else if (fe is System.Windows.Controls.ContentControl cc) cc.Content = text;
                }

                // 下拉框的选项要单独处理：ComboBox 没展开时，它的 ComboBoxItem
                // 根本不在可视化树上（虚拟化），上面那趟遍历够不着 ——
                // 实测症状是"手机出声时，电脑怎么办"那一条在英文界面里仍是中文，
                // 因为选中项的文字就显示在下拉框上。这里按 Tag 补一遍。
                if (child is System.Windows.Controls.ComboBox combo)
                {
                    foreach (var item in combo.Items)
                    {
                        if (item is System.Windows.Controls.ContentControl icc
                            && icc.Tag is string itemKey && itemKey.Length > 0)
                        {
                            icc.Content = Core.Loc.T(itemKey);
                        }
                    }
                }
            }
            ApplyLanguageTo(child);
        }
    }

    /// <summary>
    /// 切换深色 / 浅色。
    ///
    /// 立即生效，不用重启：颜色全部走 DynamicResource，换掉调色板字典后
    /// 所有引用会重新解析。正在跑的音频不受影响 —— 这里只动资源，不碰引擎。
    /// </summary>
    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        try
        {
            var mode = ThemeManager.Toggle();
            BtnTheme.Content = mode == ThemeManager.Mode.Light ? "☀" : "🌙";

            _settings.Theme = (int)mode;
            SettingsStore.Save(_settings);
            Log.Info("主题切换为：" + ThemeManager.NameOf(mode));
        }
        catch (Exception ex)
        {
            Log.Warn("切换主题失败: " + ex.Message);
        }
    }

    private void OnShowInfo(object sender, RoutedEventArgs e)
    {
        // ══════════ 用户版 / 开发者版的差别都在这里 ══════════
        //
        // 用户版只说"用户看得懂、且真的需要"的东西：连哪个地址、连接码是多少、日志在哪。
        //   —— 端口号、采样格式、采集设备名对普通用户是纯噪音，
        //      而且"采集格式：32 bit IEEFloat"这种字眼会让人以为软件很复杂或者出了问题。
        // 开发者版保留全部，并附最近日志尾巴 —— 排查时不用再去翻文件。
        string text;
        if (Core.Edition.IsDeveloper)
        {
            text = string.Join(Environment.NewLine, new[]
            {
                "【开发者版】",
                "本机地址：" + string.Join(" / ", NetworkReceiver.ListLocalAddresses()),
                "控制端口：" + _settings.Port + "   音频端口：" + (_settings.Port + 1),
                "连接码：" + _settings.PairCode,
                "输出设备：" + _engine.OutputDeviceName,
                "输出格式：" + _engine.OutputFormat,
                "采集设备：" + _engine.CaptureDeviceName,
                "采集格式：" + _engine.CaptureFormat,
                "日志文件：" + Log.LogPath,
                "",
                "最近日志：",
            }.Concat(Log.Tail(8)));
        }
        else
        {
            text = string.Join(Environment.NewLine, new[]
            {
                "手机要填的电脑地址：" + string.Join(" 或 ", NetworkReceiver.ListLocalAddresses()),
                "连接码：" + _settings.PairCode,
                "",
                "遇到问题怎么办：",
                "把下面这个文件发给开发者就行，里面记录了运行过程。",
                Log.LogPath,
            });
        }

        var result = MessageBox.Show(
            text + "\n\n" +
            "【是】打开日志文件夹\n" +
            "【否】关闭\n" +
            "【取消】重新显示开屏风险提示（撤回之前的「永久同意」）",
            "NlueMix 运行信息",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Log.LogPath + "\"") { UseShellExecute = true }); }
            catch { }
        }
        else if (result == MessageBoxResult.Cancel)
        {
            // ★ 撤回入口：这是《个人信息保护法》第 15 条要求的「撤回同意」权。
            //   只给「永久关闭」而不给重新打开，会落进《消费者权益保护法》第 26 条的
            //   「排除消费者权利」，条款会被判无效。
            _settings.SkipRiskNotice = false;
            SettingsStore.Save(_settings);

            var win = new RiskNoticeWindow { Owner = this };
            win.ShowDialog();     // 让用户当场就能重新看一遍

            if (win.SkipFromNowOn)
            {
                _settings.SkipRiskNotice = true;
                _settings.RiskNoticeAcceptedVersion = SettingsStore.NoticeVersion;
            }
            else if (win.Agreed)
            {
                _settings.RiskNoticeAcceptedVersion = SettingsStore.NoticeVersion;
            }
            SettingsStore.Save(_settings);

            MessageBox.Show(this,
                "已重新显示风险提示。\n\n" +
                "另外说明：「永久关闭」只是不再弹这个窗口，你随时可以用同样的方式重新查看并撤回同意。",
                "NlueMix", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Log.Info("MainWindow: 正在关闭");
        try
        {
            _uiTimer.Stop();
            _speakerTimer.Stop();
            SettingsStore.Save(_settings);
            _receiver.Dispose();
            _engine.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("退出清理出错: " + ex.Message);
        }
    }
}

/// <summary>界面上的一路手机音源。</summary>
public sealed class SourceRow : INotifyPropertyChanged
{
    private readonly PhoneSession _session;

    public SourceRow(int slot, PhoneSession session)
    {
        Slot = slot;
        _session = session;
        Volume = 100;
    }

    public int Slot { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<int, float>? VolumeChanged;

    public string Icon => "📱";
    public string Name { get; private set; } = Core.Loc.T("sources.waiting");
    public string Detail { get; private set; } = "";
    public string Badge { get; private set; } = "";
    public Visibility BadgeVisibility { get; private set; } = Visibility.Collapsed;
    public bool IsOnline { get; private set; }
    public Brush StatusBrush { get; private set; } = Brushes.Gray;
    public double LevelPercent { get; private set; }
    public string LevelText { get; private set; } = "";
    public bool Muted { get; set; }
    public string MuteIcon => Muted ? "🔇" : "🔊";

    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(_volume - value) < 0.01) return;
            _volume = value;
            Raise(nameof(Volume));
            if (!Muted) VolumeChanged?.Invoke(Slot, (float)(value / 100.0));
        }
    }

    public void Refresh()
    {
        IsOnline = _session.Online;
        Name = _session.Online || _session.Tcp is not null ? _session.DeviceName : Core.Loc.T("sources.waiting");

        var details = new List<string>();
        if (_session.Online)
        {
            if (_session.SampleRate > 0) details.Add(Dsp.DescribeRate(_session.SampleRate));
            details.Add(_session.CodecName);
            if (_session.Packets > 0) details.Add("收包 " + _session.Packets);
            if (_session.LostPackets > 0) details.Add("丢包 " + _session.LostPackets);
            Badge = Core.Loc.T("sources.slot", Slot + 1);
            BadgeVisibility = Visibility.Visible;
        }
        else
        {
            details.Add(Core.Loc.T("sources.join"));
            BadgeVisibility = Visibility.Collapsed;
        }
        Detail = string.Join("  ·  ", details);

        double db = _session.LevelDb;
        LevelPercent = db <= -60 ? 0 : Math.Clamp((db + 60) / 60.0 * 100, 0, 100);
        LevelText = IsOnline ? (db <= -60 ? Core.Loc.T("sources.quiet") : (int)db + " dB") : "";

        StatusBrush = IsOnline
            ? (_session.Detector.IsLoud ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                                        : new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)))
            : new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x78));

        Raise(nameof(Name));
        Raise(nameof(Detail));
        Raise(nameof(Badge));
        Raise(nameof(BadgeVisibility));
        Raise(nameof(IsOnline));
        Raise(nameof(StatusBrush));
        Raise(nameof(LevelPercent));
        Raise(nameof(LevelText));
        Raise(nameof(MuteIcon));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
