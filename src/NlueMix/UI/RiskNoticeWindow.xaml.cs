using System.Windows;
using System.Windows.Input;
using NlueMix.Core;

namespace NlueMix.UI;

/// <summary>
/// 开屏风险提示窗口。
///
/// 设计要点（有意为之，不是随手写的）：
///  · 每次启动都弹，除非用户主动选了「永久关闭」。
///  · 只有「永久关闭」那一个按钮才会写 SkipRiskNotice=true，
///    也就是说关闭这个弹窗本身就等于永久同意 —— 用户不会在没看的情况下被静默跳过。
///  · 正文、副标题、按钮全部 13-15px，没有小字。
///  · 不同意就直接退出，不弹二次挽留、不诱导。
///  · 「永久关闭」之后可以在「设置 → 关于 → 重新显示风险提示」里撤回，
///    这是《个人信息保护法》第 15 条要求的撤回权。
/// </summary>
public partial class RiskNoticeWindow : Window
{
    /// <summary>用户点了「我已阅读并同意」或「永久关闭」。</summary>
    public bool Agreed { get; private set; }

    /// <summary>用户选了「永久关闭」——调用方据此写 settings.SkipRiskNotice = true。</summary>
    public bool SkipFromNowOn { get; private set; }

    public RiskNoticeWindow()
    {
        InitializeComponent();

        // 文案统一从 RiskNotice 取，界面不硬编码，避免两处不一致
        TxtTitle.Text = RiskNotice.Title;
        TxtSubtitle.Text = RiskNotice.Subtitle;
        TxtBody.Text = RiskNotice.Body;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Agreed = false; Close(); }
        };
    }

    private void OnAgree(object sender, RoutedEventArgs e)
    {
        if (ChkRead.IsChecked != true)
        {
            // 不阻止，只是提醒 —— 拦着不让点会变成另一种形式的强迫
            MessageBox.Show(this,
                "请先勾选下面的确认框，表示你确实看过这些内容。\n\n" +
                "（如果不想看，直接关掉窗口即可，软件不会做任何事。）",
                "还差一步", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Agreed = true;
        SkipFromNowOn = false;
        Close();
    }

    private void OnClosePermanently(object sender, RoutedEventArgs e)
    {
        if (ChkRead.IsChecked != true)
        {
            MessageBox.Show(this,
                "「永久关闭」等于永久同意本页全部内容，所以请先勾选确认框。\n\n" +
                "之后想再看，可以在「设置 → 关于 → 重新显示风险提示」里重新打开。",
                "还差一步", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Agreed = true;
        SkipFromNowOn = true;
        Close();
    }
}
