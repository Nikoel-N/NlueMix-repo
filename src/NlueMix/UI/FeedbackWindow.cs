using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NlueMix.Core;

// 本项目同时开了 UseWPF 和 UseWindowsForms（蓝牙枚举要用 WinForms 那边的 API），
// 于是 TextBox / Button / Clipboard / Orientation 这些名字在两个命名空间里都有，
// 直接用会 CS0104 歧义。这里统一别名到 WPF 版本 —— 比在每个使用点写全名干净得多。
// 只给真正会歧义的几个起别名。TextBlock / Button / StackPanel 这些
// WPF 的全局 using 已经能唯一确定，再起别名反而报 CS1537「别名已存在」。
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Clipboard = System.Windows.Clipboard;

namespace NlueMix.UI;

/// <summary>
/// 反馈窗口。
///
/// 为什么单独做一个窗口而不是弹 MessageBox：
///   MessageBox 里没法给出可点击的邮箱、也没法一键复制或打开日志目录。
///   反馈入口的价值就在于"让用户少费劲" —— 他想报告问题时，
///   应该点一下就能写信、并且顺手把日志带上，而不是自己去翻路径。
///
/// 注意命名空间：本项目同时引用了 WinForms 和 Drawing，
///   FontFamily / HorizontalAlignment 都会和 System.Drawing / Window 自身的属性撞名，
///   所以这里一律写全名（System.Windows.Media.FontFamily、System.Windows.HorizontalAlignment）。
/// </summary>
internal sealed class FeedbackWindow : Window
{
    /// <summary>开发者的联系邮箱。多个地址都显示出来，用户可以任选。</summary>
    public static readonly string[] Emails =
    {
        "lihongxiao926@gmail.com",
        "13421818752@163.com",
    };

    public FeedbackWindow()
    {
        Title = Loc.T("fb.title");
        Width = 560;
        Height = 470;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Bg"]!;
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"]!;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var textBrush = (Brush)Application.Current.Resources["Text"]!;
        var dimBrush = (Brush)Application.Current.Resources["TextDim"]!;
        var faintBrush = (Brush)Application.Current.Resources["TextFaint"]!;
        var accentBrush = (Brush)Application.Current.Resources["Accent"]!;

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("fb.title"), FontSize = 20,
            FontWeight = FontWeights.SemiBold, Foreground = textBrush,
        });

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("fb.intro"), Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap, Foreground = dimBrush,
        });

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("fb.email"), Margin = new Thickness(0, 20, 0, 6),
            FontWeight = FontWeights.SemiBold, Foreground = faintBrush,
        });

        // 邮箱做成可点的链接：点一下直接用系统默认邮件客户端写
        foreach (var mail in Emails)
        {
            var link = new TextBlock
            {
                Text = mail, FontSize = 14, Foreground = accentBrush,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = Loc.T("fb.send"),
            };
            var captured = mail;
            link.MouseLeftButtonUp += (_, _) => OpenMail(captured);
            root.Children.Add(link);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var send = MakeButton(Loc.T("fb.send"), true);
        send.Click += (_, _) => OpenMail(string.Join(",", Emails));
        var copy = MakeButton(Loc.T("fb.copy"), false);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, Emails));
                // 就地回执：复制成功与否在界面上毫无反馈，用户只能自己去粘贴试一次。
                copy.Content = Loc.T("fb.copied");
            }
            catch { }
        };
        row.Children.Add(send);
        row.Children.Add(copy);
        root.Children.Add(row);

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("fb.loghint"), Margin = new Thickness(0, 22, 0, 6),
            TextWrapping = TextWrapping.Wrap, Foreground = dimBrush,
        });

        root.Children.Add(new TextBox
        {
            Text = Log.LogPath, IsReadOnly = true,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["MonoFont"]!,
            FontSize = 11.5, Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)Application.Current.Resources["Field"]!,
            Foreground = textBrush,
            BorderBrush = (Brush)Application.Current.Resources["Line"]!,
            BorderThickness = new Thickness(1),
        });

        var openLog = MakeButton(Loc.T("fb.openlog"), false);
        openLog.Margin = new Thickness(0, 10, 0, 0);
        openLog.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + Log.LogPath + "\"",
                    UseShellExecute = true,
                });
            }
            catch { }
        };
        root.Children.Add(openLog);

        var close = MakeButton(Loc.T("fb.close"), false);
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 18, 0, 0);
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "GhostButton");
        return b;
    }

    /// <summary>
    /// 用系统默认邮件客户端写信。
    /// 用 mailto: 而不是自己去连 SMTP —— 后者要用户的邮箱密码，这种事绝对不能做。
    /// </summary>
    private static void OpenMail(string to)
    {
        try
        {
            var ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
            var subject = Uri.EscapeDataString("NlueMix " + Edition.Name + " v" + ver);
            Process.Start(new ProcessStartInfo
            {
                FileName = "mailto:" + to + "?subject=" + subject,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}

/// <summary>语言选择窗口。列出八种语言，点击立即生效。</summary>
internal sealed class LanguageWindow : Window
{
    public LanguageWindow()
    {
        Title = Loc.T("lang.title");
        Width = 360;
        Height = 440;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Bg"]!;
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"]!;

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        var textBrush = (Brush)Application.Current.Resources["Text"]!;
        var faintBrush = (Brush)Application.Current.Resources["TextFaint"]!;

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("lang.title"), FontSize = 17,
            FontWeight = FontWeights.SemiBold, Foreground = textBrush,
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("lang.hint"), Margin = new Thickness(0, 6, 0, 12),
            Foreground = faintBrush,
        });

        foreach (var (code, native) in Loc.Languages)
        {
            bool current = code == Loc.CurrentCode;
            var b = new Button
            {
                Content = (current ? "●  " : "    ") + native,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.SetResourceReference(StyleProperty, current ? "PrimaryButton" : "GhostButton");

            var captured = code;
            b.Click += (_, _) =>
            {
                Loc.SetLanguage(captured);
                if (Owner is MainWindow mw) mw.ApplyLanguage();
                Close();
            };
            root.Children.Add(b);
        }

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
