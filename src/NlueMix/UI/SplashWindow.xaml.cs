using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NlueMix.UI;

/// <summary>
/// 开屏动画窗口。
///
/// 想做的效果：LOGO 先缩放淡入，然后 "NlueMix" 一个一个字母"写"出来，最后淡出。
///
/// 实现要点：
///   1. 字母不是一整块文字，而是【每个字母一个 TextBlock】放在横向 StackPanel 里。
///      只有拆开才能做错位动画；整块文字没有任何办法"逐字母"动。
///   2. 动画只改 Opacity 和 RenderTransform（TranslateTransform），
///      这两个属性走渲染线程，不触发布局，所以再慢的机器也不会卡。
///   3. 全部用 Storyboard 的 BeginTime 做错位，不用 Task.Delay 串起来 ——
///      后者在 UI 线程上排时序容易抖。
///   4. 结束后用 Completed 事件淡出并关闭，不阻塞主窗口创建。
/// </summary>
public partial class SplashWindow : Window
{
    private const string Brand = "NlueMix";
    private const int LetterDelayMs = 95;      // 相邻字母的错位间隔
    private const int LetterDurMs = 420;       // 单个字母的时长

    public SplashWindow()
    {
        InitializeComponent();
        BuildLetters();
        Loaded += (_, _) => Play();

        // 点一下 / 按任意键就跳过 —— 每次启动都强制等 1.6 秒会让人烦。
        // "可以跳过"是这类开屏动画的通行做法，Apple/Google 系的启动画面都允许点击略过。
        MouseDown += (_, _) => Close();
        KeyDown += (_, _) => Close();
    }

    /// <summary>把品牌名拆成单个字母的 TextBlock。</summary>
    private void BuildLetters()
    {
        for (int i = 0; i < Brand.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = Brand[i].ToString(),
                // 注意：System.Drawing 里也有 FontFamily / Point，本文件两个命名空间都在，
                // 不写全名会 CS0104 歧义。这种地方必须显式限定。
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
                FontSize = 34,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF9)),
                Margin = new Thickness(0),
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, 16),
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            };
            Letters.Children.Add(tb);
        }
    }

    private void Play()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // ── LOGO：缩放 + 淡入 ──
        var logoFade = new DoubleAnimation(0, 1, Ms(420)) { EasingFunction = ease };
        LogoBox.BeginAnimation(OpacityProperty, logoFade);

        var sx = new DoubleAnimation(0.7, 1, Ms(520)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        var sy = sx.Clone();
        LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);

        // ── 字母：逐个滑入 + 淡入 ──
        for (int i = 0; i < Letters.Children.Count; i++)
        {
            if (Letters.Children[i] is not TextBlock tb) continue;
            int begin = 260 + i * LetterDelayMs;

            var fade = new DoubleAnimation(0, 1, Ms(LetterDurMs))
            {
                BeginTime = At(begin),
                EasingFunction = ease,
            };
            tb.BeginAnimation(OpacityProperty, fade);

            var slide = new DoubleAnimation(16, 0, Ms(LetterDurMs))
            {
                BeginTime = At(begin),
                EasingFunction = ease,
            };
            if (tb.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // ── 整体淡出后关闭 ──
        // 原来这里还有一段副标题淡入；标语按要求去掉后那段动画也删了 ——
        // 否则会对一个已经不存在的控件做动画，编译期就报错。
        int total = 260 + Brand.Length * LetterDelayMs + 420;
        var outFade = new DoubleAnimation(1, 0, Ms(380))
        {
            BeginTime = At(total),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        outFade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, outFade);
    }

    /// <summary>用于动画时长（DoubleAnimation 构造函数的第 3 个参数要 Duration）。</summary>
    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// 用于 BeginTime（它要的是 TimeSpan?，不是 Duration —— 这两者不能隐式互转，
    /// 混用会报 CS0029。所以这里刻意分成两个函数，不图省事合成一个）。
    /// </summary>
    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);
}
