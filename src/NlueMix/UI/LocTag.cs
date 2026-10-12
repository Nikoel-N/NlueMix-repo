using System.Windows;

namespace NlueMix.UI;

/// <summary>
/// 给界面元素挂「提示文案的 key」。
///
/// 为什么需要它：MainWindow 已有的本地化做法是给控件打 Tag="key"，
/// 由 ApplyLanguageTo() 遍历可视化树替换 Text/Content。但一个控件只有一个 Tag，
/// 而工具条上的按钮【内容】和【提示】都要翻译，Tag 装不下两件事。
///
/// 所以提示另开一个附加属性。好处是：
///   - 可以写在 XAML 里（包括 DataTemplate 内部，那里的控件拿不到 x:Name）；
///   - 和 Tag 各管各的，不会互相覆盖；
///   - 翻译逻辑仍然只有一处（ApplyLanguageTo）。
/// </summary>
public static class LocTag
{
    public static readonly DependencyProperty ToolTipKeyProperty =
        DependencyProperty.RegisterAttached(
            "ToolTipKey", typeof(string), typeof(LocTag),
            new PropertyMetadata(null));

    public static void SetToolTipKey(DependencyObject element, string value)
        => element.SetValue(ToolTipKeyProperty, value);

    public static string? GetToolTipKey(DependencyObject element)
        => element.GetValue(ToolTipKeyProperty) as string;
}
