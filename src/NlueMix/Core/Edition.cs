namespace NlueMix.Core;

/// <summary>
/// 版本形态：用户版 / 开发者版。
///
/// 为什么要分：
///   调试期间为了方便，界面上放了不少"只对开发者有意义"的东西 ——
///   日志尾巴、诊断按钮、内部地址、采集方案细节。
///   这些对普通用户是噪音，甚至会让人误以为软件"很复杂/不稳定"。
///   但直接删掉又会让我以后没法排查。所以做成两种形态：
///     用户版   —— 界面干净，只留能用的东西，出问题走"导出日志"而不是当场显示
///     开发者版 —— 全部诊断信息可见
///
/// 怎么切换（三选一，优先级从高到低）：
///   1. 命令行 --dev        —— 临时用，不改文件
///   2. 环境变量 NLUEMIX_DEV=1
///   3. 编译期常量 DEV_BUILD（csproj 里加 -p:DefineConstants=DEV_BUILD）
///
/// 默认是【用户版】—— 发给别人的那个。
/// </summary>
public static class Edition
{
    private static bool? _isDev;

    /// <summary>是否开发者版。首次访问后结果缓存，避免每帧都读环境变量。</summary>
    public static bool IsDeveloper
    {
        get
        {
            _isDev ??= Detect();
            return _isDev.Value;
        }
    }

    /// <summary>用户版为 true。界面里用这个判断更符合阅读习惯。</summary>
    public static bool IsUser => !IsDeveloper;

    /// <summary>版本名，用于标题栏和日志。</summary>
    public static string Name => IsDeveloper ? "开发者版" : "用户版";

    private static bool Detect()
    {
#if DEV_BUILD
        return true;
#else
        // 1) 命令行
        try
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var a in args)
            {
                if (string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }

        // 2) 环境变量
        try
        {
            var v = Environment.GetEnvironmentVariable("NLUEMIX_DEV");
            if (!string.IsNullOrEmpty(v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { }

        return false;
#endif
    }
}
