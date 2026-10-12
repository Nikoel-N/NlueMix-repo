using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace NlueMix.Setup;

/// <summary>
/// NlueMix 电脑端（用户版）一键安装程序。
///
/// 为什么自己写而不用 Inno Setup / NSIS：
///   这台机器上没有装那些打包工具，而自写一个能精确控制每一步 ——
///   尤其是【装完顺手把防火墙规则建好】这件事，
///   否则用户装完第一次用就会遇到"手机连不上、电脑端日志里连一条连接尝试都没有"，
///   而那个问题的原因（防火墙规则按「程序路径 + 网络配置」匹配）普通人根本查不出来。
///
/// 程序本体以 zip 形式嵌在 exe 里，所以分发就是一个文件，双击即装。
/// </summary>
internal static class Program
{
    private const string AppName = "NlueMix";
    private const string ExeName = "NlueMix.exe";
    private const string PayloadResource = "app.zip";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --silent：不弹界面，直接装完退出。
        // 用途有两个：批量部署；以及自动化测试时不用去点 UI。
        if (args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ExtractPayload(_ => { });
                CreateShortcut(DesktopLnk, "NlueMix —— 手机声音送到电脑音响");
                CreateShortcut(StartMenuLnk, "NlueMix");
                ApplyFirewall();
                Console.WriteLine("OK");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex.Message);
                Environment.Exit(1);
            }
        }

        Application.Run(new SetupForm());
    }

    /// <summary>安装目录：用户自己的 LocalAppData，不需要管理员也能写（但我们本来就是管理员）。</summary>
    internal static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    internal static string ExePath => Path.Combine(InstallDir, ExeName);

    /// <summary>把嵌入的 zip 解到安装目录。</summary>
    internal static void ExtractPayload(Action<string> log)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("安装包损坏：找不到内嵌的程序文件。");

        // 直接从嵌入流解压。
        // 早先的写法是"先写一个临时 zip 再打开它"，实测在某些环境下
        // 临时目录写入会被拒（Access to the path ... is denied），
        // 而且那一步本来就是多余的 —— 流已经在手上，没必要落地再读回来。
        Directory.CreateDirectory(InstallDir);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        int n = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.Combine(InstallDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));

            // 防目录穿越：条目名里带 .. 的一律拒绝
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            n++;
        }
        log("已释放 " + n + " 个文件 → " + InstallDir);
    }

    /// <summary>
    /// 建快捷方式。用 WScript.Shell 这个 COM 组件 —— 它是 Windows 自带的，
    /// 比自己拼 .lnk 二进制格式可靠得多。
    /// </summary>
    internal static void CreateShortcut(string lnkPath, string description)
    {
        var dir = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统缺少 WScript.Shell，无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = ExePath;
        lnk.WorkingDirectory = InstallDir;
        lnk.Description = description;
        lnk.IconLocation = ExePath + ",0";
        lnk.Save();
    }

    internal static string DesktopLnk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    internal static string StartMenuLnk => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    /// <summary>
    /// 建防火墙放行规则。
    ///
    /// 这一步是安装程序最该做的事，也是最容易被忽略的：
    ///   防火墙的「程序规则」按【程序路径 + 网络配置】匹配。
    ///   规则只覆盖了某个网络配置、而用户网卡不是那个配置时，手机的连接会被系统
    ///   静默丢掉 —— 手机说连不上，电脑端日志里连一条连接尝试都没有，无从查起。
    ///   所以这里直接覆盖全部网络配置（ Domain/Private/Public ）。
    /// </summary>
    internal static string ApplyFirewall()
    {
        var sb = new StringBuilder();
        int tcp = 47820, udp = 47821;

        // 老规则名（历史安装脚本建的）也一并扩到全配置，避免新旧规则打架
        RunNetsh("advfirewall firewall set rule name=\"nluemix.exe\" new profile=any enable=yes", sb);

        EnsureRule("NlueMix 控制端口 TCP " + tcp, "TCP", tcp, sb);
        EnsureRule("NlueMix 音频端口 UDP " + udp, "UDP", udp, sb);
        return sb.ToString();
    }

    private static void EnsureRule(string name, string proto, int port, StringBuilder sb)
    {
        RunNetsh("advfirewall firewall delete rule name=\"" + name + "\"", sb);
        RunNetsh("advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow "
                 + "protocol=" + proto + " localport=" + port + " profile=any "
                 + "program=\"" + ExePath + "\" enable=yes", sb);
    }

    private static void RunNetsh(string args, StringBuilder sb)
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
                // netsh 输出 UTF-8；不指定的话中文错误信息会变成乱码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            var outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(20000);
            if (p.ExitCode != 0 && outp.Length > 0) sb.AppendLine("  " + outp);
        }
        catch (Exception ex)
        {
            sb.AppendLine("  " + ex.Message);
        }
    }
}
