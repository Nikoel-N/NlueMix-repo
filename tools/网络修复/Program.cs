using System.Diagnostics;
using System.Text;

namespace NlueMix.FirewallFixer;

/// <summary>
/// NlueMix 网络修复工具。
///
/// 它解决的问题：Windows 防火墙的「程序规则」是按「程序路径 + 网络配置」匹配的。
/// 安装时建的规则只覆盖 Public；如果之后网卡被系统重新识别成 Private
/// （换路由器、系统重判网络类型都会发生），规则就不再匹配 ——
/// 手机的入站连接被静默丢掉，表现就是手机端只说「连接不上电脑」，
/// 电脑端日志里连一条连接尝试都没有。
/// 手工去「高级安全 Windows Defender 防火墙」里找那条规则、改它的配置文件，
/// 路径很深，所以做成双击就能跑的工具。
///
/// 为什么需要管理员：改防火墙规则本身就需要管理员权限。
/// 本程序在清单里声明了 requireAdministrator，双击会自动弹一次 UAC，
/// 用户不用自己去开管理员命令行。
/// </summary>
internal static class Program
{
    private const int ControlPort = 47820;   // TCP：握手与心跳
    private const int AudioPort = 47821;     // UDP：音频

    private const string LegacyRule = "nluemix.exe";
    private const string TcpRuleName = "NlueMix 控制端口 TCP 47820";
    private const string UdpRuleName = "NlueMix 音频端口 UDP 47821";

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Console.WriteLine("========================================================");
        Console.WriteLine("  NlueMix 网络修复工具");
        Console.WriteLine("========================================================");
        Console.WriteLine();
        Console.WriteLine("正在检查 NlueMix 的防火墙放行情况...");
        Console.WriteLine();

        // 0) 先说明当前网络配置，这是「为什么突然连不上」的答案
        ShowNetworkProfiles();

        // 1) 找 NlueMix.exe
        var exe = FindNlueMixExe();
        if (exe is null)
        {
            Console.WriteLine("[!] 没找到 NlueMix.exe。找过这些位置：");
            foreach (var c in ExeCandidates()) Console.WriteLine("      " + c);
            Console.WriteLine();
            Console.WriteLine("    老规则不依赖路径，仍然可以修：");
            FixLegacyRule();
            Pause();
            return 1;
        }
        Console.WriteLine("[OK] 找到 NlueMix：" + exe);
        Console.WriteLine();

        var failures = 0;

        // 2) 修那条只覆盖 Public 的老规则
        failures += FixLegacyRule();

        // 3) 按真实路径重建两条端口规则（覆盖全部网络配置）
        Console.WriteLine();
        Console.WriteLine("按实际路径重建端口规则（覆盖 域/专用/公用 全部网络配置）：");
        failures += EnsurePortRule(TcpRuleName, "TCP", ControlPort, exe);
        failures += EnsurePortRule(UdpRuleName, "UDP", AudioPort, exe);

        // 4) 回读验证
        Console.WriteLine();
        Console.WriteLine("---- 验证 ----");
        var tcpOk = RuleCoversAllProfiles(TcpRuleName);
        var udpOk = RuleCoversAllProfiles(UdpRuleName);
        Console.WriteLine((tcpOk ? "[OK] " : "[XX] ") + TcpRuleName + " 覆盖全部配置");
        Console.WriteLine((udpOk ? "[OK] " : "[XX] ") + UdpRuleName + " 覆盖全部配置");
        if (!tcpOk || !udpOk) failures++;

        Console.WriteLine();
        Console.WriteLine("========================================================");
        if (failures == 0)
        {
            Console.WriteLine("  完成。回手机上重新点一次「开始串流」应该就能连上了。");
        }
        else
        {
            Console.WriteLine("  有 " + failures + " 项没成功。可能原因：");
            Console.WriteLine("    - 组策略锁定了防火墙设置（公司电脑常见）");
            Console.WriteLine("    - 第三方安全软件接管了防火墙");
            Console.WriteLine("    - NlueMix.exe 不在预期的位置");
            Console.WriteLine();
            Console.WriteLine("  手工办法：高级安全 Windows Defender 防火墙 -> 入站规则，");
            Console.WriteLine("  找到上面两条规则，把「配置文件」勾选成全部，");
            Console.WriteLine("  并把「程序」指向你自己的 NlueMix.exe。");
        }
        Console.WriteLine("========================================================");

        Pause();
        return failures == 0 ? 0 : 2;
    }

    // ---------------------------------------------- 具体动作

    private static int FixLegacyRule()
    {
        Console.WriteLine("1) 把老规则「" + LegacyRule + "」扩展到所有网络配置...");

        var (showCode, showOut) = RunNetsh("advfirewall firewall show rule name=\"" + LegacyRule + "\"");
        if (showCode != 0 || showOut.Length == 0)
        {
            Console.WriteLine("   （没有这条规则，跳过 —— 下面会新建）");
            return 0;
        }

        var (code, output) = RunNetsh(
            "advfirewall firewall set rule name=\"" + LegacyRule + "\" new profile=any enable=yes");

        if (code != 0)
        {
            Console.WriteLine("   [XX] 失败：" + output.Trim());
            return 1;
        }
        Console.WriteLine("   [OK] 已扩展到全部网络配置");
        return 0;
    }

    private static int EnsurePortRule(string name, string proto, int port, string exe)
    {
        // 先删后建，保证幂等（删不到也无所谓）
        RunNetsh("advfirewall firewall delete rule name=\"" + name + "\"");

        var (code, output) = RunNetsh(
            "advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow " +
            "protocol=" + proto + " localport=" + port + " profile=any program=\"" + exe + "\" enable=yes");

        if (code != 0)
        {
            Console.WriteLine("   [XX] " + name + " 失败：" + output.Trim());
            return 1;
        }
        Console.WriteLine("   [OK] " + name + "  （" + proto + " " + port + "）");
        return 0;
    }

    private static bool RuleCoversAllProfiles(string name)
    {
        var (code, text) = RunNetsh("advfirewall firewall show rule name=\"" + name + "\"");
        if (code != 0 || text.Length == 0) return false;
        // netsh 按系统语言输出：英文 Any / 中文 任何
        return text.Contains("Any", StringComparison.OrdinalIgnoreCase)
            || text.Contains("任何", StringComparison.Ordinal);
    }

    private static void ShowNetworkProfiles()
    {
        Console.WriteLine("---- 当前网络配置 ----");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"Get-NetConnectionProfile | Select-Object InterfaceAlias,NetworkCategory | Format-Table -HideTableHeaders | Out-String\"",
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
                    if (t.Length > 0) Console.WriteLine("   " + t);
                }
            }
        }
        catch { Console.WriteLine("   （读不到，跳过）"); }

        Console.WriteLine();
        Console.WriteLine("   防火墙规则要匹配上网卡当前的配置才会生效。");
        Console.WriteLine("   网卡配置一变（换路由器、系统重判网络类型），老规则就可能失效。");
        Console.WriteLine();
    }

    // ---------------------------------------------- 工具函数

    private static IEnumerable<string> ExeCandidates()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "NlueMix", "NlueMix.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "NlueMix.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NlueMix", "NlueMix.exe");
    }

    private static string? FindNlueMixExe()
    {
        foreach (var c in ExeCandidates())
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
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

    private static void Pause()
    {
        Console.WriteLine();
        Console.Write("按回车键关闭...");
        try { Console.ReadLine(); } catch { }
    }
}

