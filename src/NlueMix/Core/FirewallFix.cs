using System.Diagnostics;
using System.Text;

namespace NlueMix.Core;

/// <summary>
/// Windows 防火墙放行检查与修复。
///
/// 为什么需要它（实测踩到的真实故障）：
///   防火墙的「程序规则」是按「程序路径 + 网络配置」匹配的。
///   安装时建的规则只覆盖了 Public，当时两个网卡恰好都是 Public，所以一直好用。
///   后来以太网被 Windows 重新识别成 Private，规则就不再匹配 ——
///   手机的入站连接被静默丢掉，手机端只显示「连接不上电脑」，
///   电脑端日志里连一条连接尝试都看不到。用户完全无从判断。
///
/// 怎么修：把规则扩展到 profile=any，并按当前真实的 exe 路径重建端口规则。
///   改防火墙需要管理员权限，所以走「以管理员身份重启自己」——
///   见 <see cref="BuildElevatedSelfCommand"/>。
/// </summary>
public static class FirewallFix
{
    /// <summary>安装时创建的老规则名。它只覆盖 Public，需要扩展。</summary>
    public const string LegacyRule = "nluemix.exe";

    /// <summary>我们自己维护的端口规则名。</summary>
    public const string TcpRuleName = "NlueMix 控制端口 TCP 47820";
    public const string UdpRuleName = "NlueMix 音频端口 UDP 47821";

    public const int ControlPort = 47820;   // TCP：握手与心跳
    public const int AudioPort = 47821;     // UDP：音频

    /// <summary>以管理员身份重启自己并跑修复时用的命令行参数。</summary>
    public const string FixArg = "--fix-firewall";

    /// <summary>
    /// 规则当前覆盖哪些网络配置。查不到返回空串。
    /// netsh 按系统语言输出：英文 Profiles / 中文 配置文件。
    /// </summary>
    public static string GetRuleProfiles(string ruleName = LegacyRule)
    {
        var (code, text) = RunNetsh("advfirewall firewall show rule name=\"" + ruleName + "\"");
        if (code != 0 || text.Length == 0) return "";

        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("配置文件", StringComparison.Ordinal) ||
                t.StartsWith("Profiles", StringComparison.OrdinalIgnoreCase))
            {
                var idx = t.IndexOf(':');
                if (idx < 0) idx = t.IndexOf('：');
                if (idx >= 0) return t[(idx + 1)..].Trim();
            }
        }
        return "";
    }

    /// <summary>
    /// 规则是否已覆盖所有网络配置。
    ///
    /// 注意必须同时认中英文：netsh 输出随系统语言变 ——
    ///   英文系统 Profiles: Any    中文系统 配置文件: 任何
    /// 只判 "Any" 会在中文系统上永远返回 false，于是「修好了却报告没修好」。
    /// </summary>
    public static bool CoversAllProfiles(string ruleName)
    {
        var p = GetRuleProfiles(ruleName);
        if (p.Length == 0) return false;
        return p.Contains("Any", StringComparison.OrdinalIgnoreCase)
            || p.Contains("任何", StringComparison.Ordinal);
    }

    /// <summary>老规则是否已覆盖全部配置（也就是"这次不用修"）。</summary>
    public static bool IsFullyOpen() => CoversAllProfiles(LegacyRule) && CoversAllProfiles(TcpRuleName);

    /// <summary>
    /// 完整修复：扩展老规则 + 按真实路径重建两条端口规则 + 回读验证。
    /// 返回给用户看的分行报告。必须在管理员权限下调用。
    /// </summary>
    public static string ApplyAll()
    {
        var sb = new StringBuilder();
        var failures = 0;

        // 1) 当前网络配置 —— 这是"为什么突然连不上"的答案
        sb.AppendLine("当前网络配置：");
        foreach (var line in QueryNetworkProfiles()) sb.AppendLine("   " + line);
        sb.AppendLine();

        // 2) 找到真实的 NlueMix.exe 路径
        var exe = Environment.ProcessPath;
        var isNlueMix = exe is not null &&
                        Path.GetFileName(exe).StartsWith("NlueMix", StringComparison.OrdinalIgnoreCase);
        if (isNlueMix) sb.AppendLine("NlueMix 位置：" + exe);
        else sb.AppendLine("注意：当前程序不是 NlueMix.exe（" + (exe ?? "未知") + "），端口规则将不限定程序。");
        sb.AppendLine();

        // 3) 老规则扩展到全部网络配置
        sb.AppendLine("把老规则「" + LegacyRule + "」扩展到全部网络配置…");
        var legacyProfiles = GetRuleProfiles(LegacyRule);
        if (legacyProfiles.Length == 0)
        {
            sb.AppendLine("   （没有这条规则，跳过 —— 下面会新建）");
        }
        else
        {
            var (c, o) = RunNetsh("advfirewall firewall set rule name=\"" + LegacyRule + "\" new profile=any enable=yes");
            if (c == 0) sb.AppendLine("   [成功]");
            else { sb.AppendLine("   [失败] " + o.Trim()); failures++; }
        }
        sb.AppendLine();

        // 4) 按真实路径重建端口规则
        sb.AppendLine("按当前路径重建端口规则（覆盖 域/专用/公用）：");
        failures += EnsurePortRule(sb, TcpRuleName, "TCP", ControlPort, isNlueMix ? exe : null);
        failures += EnsurePortRule(sb, UdpRuleName, "UDP", AudioPort, isNlueMix ? exe : null);
        sb.AppendLine();

        // 5) 回读验证
        var tcpOk = CoversAllProfiles(TcpRuleName);
        var udpOk = CoversAllProfiles(UdpRuleName);
        sb.AppendLine("验证：");
        sb.AppendLine((tcpOk ? "   [通过] " : "   [未通过] ") + TcpRuleName);
        sb.AppendLine((udpOk ? "   [通过] " : "   [未通过] ") + UdpRuleName);
        if (!tcpOk || !udpOk) failures++;

        sb.AppendLine();
        if (failures == 0)
        {
            sb.AppendLine("完成。回手机上重新点一次「开始串流」应该就能连上了。");
        }
        else
        {
            sb.AppendLine("有 " + failures + " 项没成功。可能原因：");
            sb.AppendLine("   · 组策略锁定了防火墙设置（公司电脑常见）");
            sb.AppendLine("   · 第三方安全软件接管了防火墙");
            sb.AppendLine("   · 程序不在预期的位置");
            sb.AppendLine();
            sb.AppendLine("手工办法：高级安全 Windows Defender 防火墙 → 入站规则，");
            sb.AppendLine("找到上面两条规则，把「配置文件」勾选成全部。");
        }

        return sb.ToString();
    }

    private static int EnsurePortRule(StringBuilder sb, string name, string proto, int port, string? exe)
    {
        RunNetsh("advfirewall firewall delete rule name=\"" + name + "\"");   // 删不到也无所谓

        var args = "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow " +
                   "protocol=" + proto + " localport=" + port + " profile=any enable=yes";
        if (exe is not null) args += " program=\"" + exe + "\"";

        var (code, output) = RunNetsh(args);
        if (code != 0)
        {
            sb.AppendLine("   [失败] " + name + "：" + output.Trim());
            return 1;
        }
        sb.AppendLine("   [成功] " + name + "（" + proto + " " + port +
                      (exe is null ? "，未限定程序" : "，限定到 NlueMix.exe") + "）");
        return 0;
    }

    private static IEnumerable<string> QueryNetworkProfiles()
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                // ★ 必须让 PowerShell 自己输出 UTF-8。
                //   Windows PowerShell 5.1 默认按控制台 OEM 代码页输出，
                //   「以太网」会变成「??̫??」—— 网卡名对用户很重要
                //   （他得认出是哪块网卡），乱码就等于没说。
                Arguments = "-NoProfile -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
                            "Get-NetConnectionProfile | ForEach-Object { $_.InterfaceAlias + '  ->  ' + $_.NetworkCategory }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                foreach (var line in outp.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length > 0) result.Add(t);
                }
            }
        }
        catch { }
        if (result.Count == 0) result.Add("（读不到）");
        return result;
    }

    private static (int Code, string Output) RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // ★ netsh 输出的是 UTF-8（和 adb.exe 一样）。
                //   不指定时 .NET 按系统 OEM 代码页（中文系统 GBK）解码，
                //   「请求的操作需要提升(作为管理员运行)」会变成
                //   「璇锋眰鐨勬搷浣滈渶瑕佹彁鍗?浣滀负绠＄悊鍛樿繍琛?」——
                //   用户看到的就是天书。实测踩到的，不是推测。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "");
            var sb = new StringBuilder();
            sb.Append(p.StandardOutput.ReadToEnd());
            sb.Append(p.StandardError.ReadToEnd());
            p.WaitForExit(20000);
            return (p.ExitCode, sb.ToString());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// 以管理员身份重启自己并跑修复。
    ///
    /// 这样做的原因：改防火墙必须管理员权限，而主程序平时不该以管理员运行
    /// （那是很坏的习惯）。所以只在需要修的时候，用 runas 起一个自己的副本，
    /// 带上 --fix-firewall 参数，跑完就退 —— 用户只看到一次 UAC。
    /// </summary>
    public static bool RunElevatedSelfFix(out string error)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                error = "拿不到自己的路径，无法提权。";
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = FixArg,
                UseShellExecute = true,   // 提权必须走 shell
                Verb = "runas",           // 触发 UAC
            };

            using var p = Process.Start(psi);
            if (p is null) { error = "启动提权进程失败。"; return false; }
            p.WaitForExit(120000);

            error = "";
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>给用户看的手工命令。</summary>
    public static string ManualCommand =>
        "netsh advfirewall firewall set rule name=\"" + LegacyRule + "\" new profile=any";
}

