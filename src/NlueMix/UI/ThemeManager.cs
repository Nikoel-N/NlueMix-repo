using System;
using System.Windows;

namespace NlueMix.UI;

/// <summary>
/// 深浅主题切换。
///
/// 原理：颜色全部放在 Theme.Dark.xaml / Theme.Light.xaml 里（key 完全一致），
/// 控件样式在 Theme.xaml 里、用 DynamicResource 引用颜色。
/// 切换时把 MergedDictionaries 里那个调色板条目换掉 —— DynamicResource 会自动重新解析，
/// 界面即时变色，不需要重建窗口、不打断正在跑的音频。
///
/// 为什么必须用 DynamicResource：
///   StaticResource 只在 XAML 加载时解析一次，之后换字典界面上什么都不会变
///   （而且不会报错，只是"没反应"，很难查）。所以颜色引用一律用 Dynamic。
///
/// 为什么把颜色单独拆成两个字典：
///   这样"主题"就是一份可以整体替换的数据，控件样式不用改两份、也不会两边走样。
/// </summary>
public static class ThemeManager
{
    /// <summary>深色 / 浅色。</summary>
    public enum Mode { Dark = 0, Light = 1 }

    private static readonly Uri DarkUri = new("UI/Theme.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("UI/Theme.Light.xaml", UriKind.Relative);

    /// <summary>当前模式。切换后立即生效。</summary>
    public static Mode Current { get; private set; } = Mode.Dark;

    /// <summary>
    /// 应用主题。可在任何时候调用。
    /// 找不到字典时静默保持原样 —— 主题是外观问题，不该让程序起不来。
    /// </summary>
    public static void Apply(Mode mode)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;

            var dicts = app.Resources.MergedDictionaries;
            var wanted = new ResourceDictionary { Source = mode == Mode.Light ? LightUri : DarkUri };

            // 找出当前那个调色板条目并原地替换。
            // 判据：字典里有 Bg 这个 key（只有调色板才有），但里面没有 Dot 这种控件样式。
            int replacedAt = -1;
            for (int i = 0; i < dicts.Count; i++)
            {
                var d = dicts[i];
                if (d.Contains("Bg") && d.Contains("Text") && !d.Contains("Dot"))
                {
                    dicts[i] = wanted;
                    replacedAt = i;
                    break;
                }
            }

            if (replacedAt < 0)
            {
                // 首次启动时还没有调色板，直接追加
                dicts.Add(wanted);
            }

            Current = mode;
        }
        catch (Exception ex)
        {
            NlueMix.Log.Warn("切换主题失败（不影响使用）: " + ex.Message);
        }
    }

    /// <summary>在深色/浅色之间切换，返回切换后的模式。</summary>
    public static Mode Toggle()
    {
        Apply(Current == Mode.Dark ? Mode.Light : Mode.Dark);
        return Current;
    }

    public static string NameOf(Mode m) => m == Mode.Light ? "浅色" : "深色";
}
