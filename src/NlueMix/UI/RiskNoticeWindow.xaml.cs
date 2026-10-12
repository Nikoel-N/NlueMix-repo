using System.Windows;
using System.Windows.Input;
using NlueMix.Core;

namespace NlueMix.UI;

/// <summary>
/// 开屏风险提示窗口。两种模式共用这一个窗口：
///
///  · 闸门模式（默认，启动时）：用户还没同意，或文案改版了。
///    必须勾选确认框；不勾就点只提醒不阻拦。关掉窗口 = 不同意，程序直接退出。
///
///  · 查看模式（review: true，主界面 📖 按钮）：随时重看这一页，
///    并且可以在这里【撤回同意】—— 撤回后程序退出。
///    这是《个人信息保护法》第 15 条要求的撤回同意权。
///
/// 一次修复（原来这里的 bug）：
///    点「我已阅读并同意」只写了 RiskNoticeAcceptedVersion，而启动闸门还要求
///    SkipRiskNotice=true，于是每次启动都重新弹，用户不点「永久关闭」就进不去。
///    那等于强迫用户做"永久同意"才能用软件 —— 正是本页设计上要避免的诱导。
///    现在同意一次就记住，文案改版才重新问；「永久关闭」这个按钮也随之取消。
///
/// 文案统一从 RiskNotice 取，界面不硬编码，避免两处不一致。
/// 提示正文目前只有中文（法律文本，不做翻译），与手机端逐字一致。
/// </summary>
public partial class RiskNoticeWindow : Window
{
    /// <summary>闸门模式：用户同意继续。</summary>
    public bool Agreed { get; private set; }

    /// <summary>查看模式：用户选择撤回同意。</summary>
    public bool Revoked { get; private set; }

    private readonly bool _review;

    public RiskNoticeWindow(bool review = false)
    {
        InitializeComponent();

        _review = review;

        TxtTitle.Text = RiskNotice.Title;
        TxtSubtitle.Text = RiskNotice.Subtitle;
        TxtBody.Text = RiskNotice.Body;

        if (review)
        {
            // 查看模式：不要求再勾一次 —— 用户是回来看内容的，不是来重新签字的
            ChkRead.Visibility = Visibility.Collapsed;
            TxtHint.Text = "这是查看模式。你可以随时回到这里重看这一页。";
            BtnAgree.Content = "关闭";
            BtnClosePermanently.Content = "撤回同意，退出程序";
        }
        else
        {
            BtnClosePermanently.Content = "不同意，退出";
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Agreed = false; Close(); }
        };
    }

    private void OnAgree(object sender, RoutedEventArgs e)
    {
        if (_review) { Close(); return; }

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
        Close();
    }

    private void OnClosePermanently(object sender, RoutedEventArgs e)
    {
        if (_review)
        {
            // 查看模式的第二个按钮 = 撤回同意
            var ok = MessageBox.Show(this,
                "撤回同意后，程序会立即退出；下次打开需要重新阅读并同意这一页。\n\n确定撤回吗？",
                "撤回同意", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;

            Revoked = true;
            Close();
            return;
        }

        // 闸门模式：不同意 → 关掉窗口，调用方据此退出程序
        Agreed = false;
        Close();
    }
}
