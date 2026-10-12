using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace NlueMix;

public partial class App : Application
{
    public static App Instance => (App)Current;

    /// <summary>命令行自检类参数：出现任意一个就表示"不要开界面"。</summary>
    private static readonly string[] HeadlessArgs = { "--selftest", "--usb-test", "--wireless-test", "--e2e-test", "--fix-firewall" };

    protected override void OnStartup(StartupEventArgs e)
    {
        // 自检模式：--selftest 时不开界面，直接跑一遍关键环节
        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SelfTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        // USB（adb）采集自检：不开界面，直接录一段手机声音看峰值
        if (e.Args.Any(a => string.Equals(a, "--usb-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = Audio.UsbCaptureTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        // 端到端验收：采集 → Wi-Fi → WASAPI → 指定输出设备
        if (e.Args.Any(a => string.Equals(a, "--e2e-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code2 = E2EPlayTest.Run(e.Args);
            Shutdown(code2);
            return;
        }

        // 无线（Wi-Fi）无损采集验收：adb 点火 + 手机回连电脑，全程无线
        if (e.Args.Any(a => string.Equals(a, "--wireless-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = Audio.WirelessTest.Run(e.Args);
            Shutdown(code);
            return;
        }

        // ── 防火墙修复模式 ──
        //
        // 这个分支是"自己以管理员身份重启自己"跑起来的那个副本。
        // 主程序平时绝不该以管理员运行（那是很坏的习惯），
        // 所以只在用户点 🛡 时，用 runas 起一个自己的副本带这个参数，
        // 跑完就退，用户只看到一次 UAC。
        if (e.Args.Any(a => string.Equals(a, "--fix-firewall", StringComparison.OrdinalIgnoreCase)))
        {
            string report;
            try
            {
                report = Core.FirewallFix.ApplyAll();
                // 报告同时写进日志：出问题时用户只要把日志发过来就行，
                // 不用凭记忆复述弹窗上写了什么。
                Log.Info("[防火墙] 修复报告：\n" + report);
            }
            catch (Exception ex)
            {
                report = "修复过程出错：\n\n" + ex.Message;
                Log.Error("[防火墙] 提权修复异常: " + ex.Message);
            }

            MessageBox.Show(report, "NlueMix 网络修复", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // ── 应用上次选的主题 ──
        // 必须在创建任何窗口之前：StaticResource 在 XAML 加载时解析，
        // 窗口一旦建出来再换字典，那些静态引用不会跟着变。
        try
        {
            var s = Core.SettingsStore.Load();
            UI.ThemeManager.Apply((UI.ThemeManager.Mode)s.Theme);
            Core.Loc.SetLanguage(s.Language);
        }
        catch (Exception ex)
        {
            Log.Warn("应用主题失败（将用深色）: " + ex.Message);
        }

        // ── 开屏风险提示 ──
        // 位置很关键：必须在创建主窗口之前。用户不同意就直接退出，
        // 不能出现"窗口已经开起来、采集已经在跑"的情况。
        if (!ShowRiskNoticeIfNeeded())
        {
            Log.Info("用户在风险提示页选择不同意，程序退出");
            Shutdown(0);
            return;
        }

        // ★ 修复：StartupUri 已从 App.xaml 移除，改为在这里显式建主窗口。
        //   原因：App.xaml 里的 StartupUri 是"无条件"生效的 —— 它会在 OnStartup 返回后
        //   照样创建 MainWindow，于是 --wireless-test / --usb-test / --selftest
        //   这些本该无界面的模式也会 new 出主窗口并崩在 Dispatcher 上
        //   （堆栈表现为 Application.RunInternal(Window)）。
        //   而 StartupUri 属性 setter 不接受 null（会抛 ArgumentNullException），
        //   所以只能把它从 XAML 里拿掉，再在这里按需创建。
        // ── 开屏动画 ──
        // 只在用户版播；开发者版跳过 —— 调试时每次多等 1.6 秒很折磨。
        // 整段包在 try 里：动画只是锦上添花，绝不能因为它起不来就开不了程序。
        if (Core.Edition.IsUser)
        {
            // ★ 关键：开屏期间必须临时改掉"最后一个窗口关闭就退出"的默认行为。
            //
            //   WPF 的 ShutdownMode 默认是 OnLastWindowClose。开屏是此刻【唯一】的窗口，
            //   它一关，整个 Application 就 Shutdown 了 —— 主窗口根本没机会创建。
            //   实测症状：开屏正常播完、然后程序直接消失、日志里连异常都没有。
            //   所以这里先切成 OnExplicitShutdown，播完再切回 OnMainWindowClose。
            var savedMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var splash = new UI.SplashWindow();
                splash.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Warn("开屏动画失败（不影响使用）: " + ex.Message);
            }
            finally
            {
                ShutdownMode = savedMode == ShutdownMode.OnExplicitShutdown
                    ? ShutdownMode.OnMainWindowClose
                    : savedMode;
            }
        }

        Log.Info("版本形态：" + Core.Edition.Name
                 + (Core.Edition.IsDeveloper ? "（--dev 或 NLUEMIX_DEV=1 可切换）" : ""));

        MainWindow = new UI.MainWindow();
        MainWindow.Show();

        // 全局兜底：任何未处理异常都不能让程序悄悄死掉
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            SafeLog("AppDomain 未处理异常", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SafeLog("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        SafeLog("NlueMix 启动，版本 " + typeof(App).Assembly.GetName().Version, null);

        // 进程级退出追踪：谁把程序关掉的，日志里必须留痕
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            SafeLog("ProcessExit 事件：进程正在退出", null);
        Exit += (_, args) =>
            SafeLog("Application.Exit 事件：退出码 " + args.ApplicationExitCode, null);
        SessionEnding += (_, args) =>
        {
            SafeLog("SessionEnding 事件：系统要关机/注销了", null);
            args.Cancel = false;
        };

        Dispatcher.InvokeAsync(() =>
        {
            SafeLog("主窗口状态：" + (MainWindow is null ? "无主窗口！" : "已创建 - " + MainWindow.Title), null);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 开屏风险提示。返回 false 表示用户不同意，调用方应退出程序。
    ///
    /// 每次启动都弹，除非用户主动选了「永久关闭此提示」。
    /// 「永久关闭」按钮本身就写着"同意"，所以跳过弹窗 = 已经同意过，两者不矛盾。
    /// </summary>
    private static bool ShowRiskNoticeIfNeeded()
    {
        try
        {
            var settings = Core.SettingsStore.Load();

            if (settings.SkipRiskNotice &&
                settings.RiskNoticeAcceptedVersion == Core.SettingsStore.NoticeVersion)
            {
                return true;   // 用户此前主动选择不再显示，且已同意当前版本
            }

            var win = new UI.RiskNoticeWindow();
            win.ShowDialog();

            if (!win.Agreed)
            {
                return false;  // 关掉窗口 = 不同意
            }

            if (win.SkipFromNowOn)
            {
                settings.SkipRiskNotice = true;
                settings.RiskNoticeAcceptedVersion = Core.SettingsStore.NoticeVersion;
                Core.SettingsStore.Save(settings);
                Log.Info("用户同意并选择永久关闭风险提示（可在设置里重新打开）");
            }
            else
            {
                // 本次同意了，但没选永久关闭 —— 下次启动还要弹。
                // 同时把"当前版本已同意"记下来，方便日后统计。
                settings.RiskNoticeAcceptedVersion = Core.SettingsStore.NoticeVersion;
                Core.SettingsStore.Save(settings);
            }

            return true;
        }
        catch (Exception ex)
        {
            // 弹窗本身出错不能把程序卡死，但也不能悄悄放行 —— 记日志后按"同意"处理，
            // 因为用户此时并没有拒绝的意思表示。
            SafeLog("显示风险提示失败，按已同意继续", ex);
            return true;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var root = Unwrap(e.Exception);
        SafeLog("界面线程异常", root);

        if (_warned) { e.Handled = true; return; }
        _warned = true;

        try
        {
            var text = new StringBuilder();
            text.AppendLine("NlueMix 遇到了一个内部错误，但程序还在运行。");
            text.AppendLine();
            text.AppendLine(root.GetType().Name + ": " + root.Message);
            text.AppendLine();
            text.AppendLine("=== 堆栈（前 12 行）===");
            var frames = (root.StackTrace ?? "(无堆栈)").Split('\n');
            for (int i = 0; i < Math.Min(12, frames.Length); i++) text.AppendLine(frames[i].Trim());
            text.AppendLine();
            text.AppendLine("日志文件：" + SafeLogPath());

            MessageBox.Show(text.ToString(), "NlueMix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // 连弹窗都失败就算了，绝不能在这里再抛异常
        }

        e.Handled = true;
    }

    /// <summary>把 TargetInvocationException 之类的外壳剥掉，拿到真正的异常。</summary>
    public static Exception Unwrap(Exception? ex)
    {
        while (ex is System.Reflection.TargetInvocationException && ex.InnerException is not null)
            ex = ex.InnerException;
        return ex ?? new Exception("未知错误");
    }

    /// <summary>写日志绝不能反过来把程序搞崩。</summary>
    private static void SafeLog(string message, Exception? ex)
    {
        try
        {
            if (ex is null) Log.Info(message);
            else Log.Error(message + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }
        catch (Exception logFailure)
        {
            try { Debug.WriteLine("[NlueMix] 写日志失败: " + logFailure.Message); } catch { }
        }
    }

    private static string SafeLogPath()
    {
        try { return Log.LogPath; } catch { return "(取日志路径也失败了)"; }
    }

    private bool _warned;
}
