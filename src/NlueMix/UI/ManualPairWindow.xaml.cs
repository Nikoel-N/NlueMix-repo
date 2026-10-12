using System.Windows;
using System.Windows.Input;
using NlueMix.Audio;
using NlueMix.Core;

namespace NlueMix.UI;

/// <summary>
/// 手动连接手机（无线调试）对话框。
///
/// 为什么需要它：自动配置（手机端无障碍读端口）在有些 ROM 上读不到页面文字，
/// 这时候不能让用户卡死 —— 让人看一眼手机、把那两个数字填进来就行。
/// 配对只需要做一次，所以"手动一次"的成本是可以接受的。
///
/// 数字在手机上的位置：
///   设置 → 更多设置 → 开发者选项 → 无线调试 → 「IP 地址和端口」
///   首次连接还需要点「使用配对码配对设备」拿 6 位配对码。
/// </summary>
public partial class ManualPairWindow : Window
{
    public ManualPairWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtIp.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void OnPairConnect(object sender, RoutedEventArgs e)
    {
        int? pairPort = null;
        string? pairCode = null;

        var pp = TxtPairPort.Text.Trim();
        if (pp.Length > 0)
        {
            if (!int.TryParse(pp, out var v) || v <= 0)
            {
                Show("配对端口不是有效数字：" + pp);
                return;
            }
            pairPort = v;
        }

        var pc = TxtPairCode.Text.Trim();
        if (pc.Length > 0) pairCode = pc;

        if (pairPort is null || pairCode is null)
        {
            Show("「配对并连接」需要同时填配对端口和 6 位配对码。\n" +
                 "如果这台电脑已经和手机配对过，直接点「仅连接」即可。");
            return;
        }

        Run(pairPort, pairCode);
    }

    private void OnConnectOnly(object sender, RoutedEventArgs e) => Run(null, null);

    private void Run(int? pairPort, string? pairCode)
    {
        var ip = TxtIp.Text.Trim();
        if (ip.Length == 0) { Show("请填手机 IP。"); return; }

        if (!int.TryParse(TxtConnectPort.Text.Trim(), out var connectPort) || connectPort <= 0)
        {
            Show("请填连接端口（无线调试页面「IP 地址和端口」里冒号后面那串数字）。");
            return;
        }

        SetBusy(true);
        Show("正在执行…");

        // adb 是阻塞调用，不能卡界面线程
        Task.Run(() =>
        {
            string output;
            bool ok;
            try
            {
                var adb = new AdbCapture();
                ok = adb.PairAndConnect(ip, connectPort, pairPort, pairCode, out var message);
                output = message;
            }
            catch (Exception ex)
            {
                ok = false;
                output = "出错了：" + ex.Message;
            }

            Dispatcher.Invoke(() =>
            {
                SetBusy(false);
                var head = ok ? "✓ 成功\n\n" : "✗ 失败\n\n";
                var tail = string.Join("\n", Log.Tail(12));
                Show(head + output + "\n\n───── 最近日志 ─────\n" + tail);
            });
        });
    }

    private void SetBusy(bool busy)
    {
        BtnPairConnect.IsEnabled = !busy;
        BtnConnectOnly.IsEnabled = !busy;
        // 注意要完全限定：System.Windows.Forms 里也有 Cursors，直接写会歧义
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void Show(string text) => TxtResult.Text = text;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>给调用方取用户填的手机 IP（用来记住，下次省得重输）。</summary>
    public string GetEnteredIp() => TxtIp.Text.Trim();
}
