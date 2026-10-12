using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

// NlueMix 一键安装程序
// 自己做全部安装工作（解包 → 复制 → 快捷方式 → 卸载脚本），不依赖任何批处理

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "NlueMix 安装程序";

// ★ 必须注册代码页编码提供程序，否则下面 Encoding.GetEncoding(936) 会抛
//   NotSupportedException("No data is available for encoding 936")。
//
//   .NET Core / .NET 5+ 只自带 UTF-8/UTF-16/ASCII 等少数编码，GBK(936) 这类
//   传统代码页要显式注册。少了这一句的后果不是"中文显示成乱码"那么轻 ——
//   是 WriteUninstall() 直接抛异常，安装程序走到 [3/4] 就整个失败退出，
//   用户装完看不到"安装成功"，也拿不到卸载脚本。
//   System.Text.Encoding.CodePages 就在 .NET 共享框架里，不需要额外包。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string targetDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs", "NlueMix");
string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
string startMenu = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Microsoft", "Windows", "Start Menu", "Programs");

bool silent = args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase));

try
{
    Banner();

    // ── 1. 关掉正在运行的 NlueMix
    foreach (var p in Process.GetProcessesByName("NlueMix"))
    {
        try { p.Kill(); p.WaitForExit(3000); } catch { }
    }
    Thread.Sleep(600);

    // ── 2. 清空旧版本
    if (Directory.Exists(targetDir))
    {
        try { Directory.Delete(targetDir, true); } catch { }
    }
    Directory.CreateDirectory(targetDir);

    // ── 3. 解包内嵌文件到目标目录（直接写，不经过临时目录）
    Console.WriteLine("  正在解包程序文件...");
    int fileCount = 0;
    var asm = Assembly.GetExecutingAssembly();
    using (var stream = asm.GetManifestResourceStream("payload.zip"))
    {
        if (stream == null) throw new Exception("安装包损坏：找不到内嵌数据");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(targetDir, rel);
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(dest, true);
            fileCount++;
        }
    }
    Console.WriteLine("  [1/4] 已释放 " + fileCount + " 个文件");

    string exe = Path.Combine(targetDir, "NlueMix.exe");
    if (!File.Exists(exe)) throw new Exception("程序文件不完整（缺少 NlueMix.exe）");

    // ── 4. 快捷方式
    string ico = Path.Combine(targetDir, "NlueMix.ico");
    if (!File.Exists(ico)) ico = exe;
    MakeShortcut(Path.Combine(desktop, "NlueMix.lnk"), exe, targetDir, ico);
    MakeShortcut(Path.Combine(startMenu, "NlueMix.lnk"), exe, targetDir, ico);
    Console.WriteLine("  [2/4] 桌面和开始菜单快捷方式已创建");

    // ── 5. 卸载脚本
    WriteUninstall(targetDir);
    Console.WriteLine("  [3/4] 卸载程序已生成");

    // ── 6. 完成
    Console.WriteLine("  [4/4] 安装完成");
    Console.WriteLine();
    Console.WriteLine("  ==========================================");
    Console.WriteLine("      安装成功！");
    Console.WriteLine("  ==========================================");
    Console.WriteLine();
    Console.WriteLine("   程序位置：" + targetDir);
    Console.WriteLine("   桌面上已经有 NlueMix 图标了");
    Console.WriteLine("   卸载：运行程序目录里的 卸载.bat");
    Console.WriteLine();

    if (!silent)
    {
        Console.Write("  现在就启动 NlueMix 吗？(Y/N) ");
        var key = SafeReadLine();
        if (!string.Equals(key, "n", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = targetDir, UseShellExecute = true }); }
            catch { }
        }
        Console.WriteLine();
        SafePause();
    }
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("  [失败] " + ex.Message);
    Console.WriteLine();
    Console.WriteLine(ex.ToString());
    Console.WriteLine();
    SafePause();
    return 1;
}

// ─────────────────────────────────────────────────────────

static void Banner()
{
    Console.WriteLine();
    Console.WriteLine("  ==========================================");
    Console.WriteLine("     NlueMix  蓝牙串流混音器  安装程序");
    Console.WriteLine("  ==========================================");
    Console.WriteLine();
}

static void SafePause()
{
    try
    {
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("  按回车键关闭...");
            Console.ReadLine();
        }
    }
    catch { }
}

static string SafeReadLine()
{
    try { return Console.IsInputRedirected ? "" : (Console.ReadLine() ?? ""); }
    catch { return ""; }
}

// 用 WScript.Shell 建快捷方式（不用引 COM 引用，动态调用就行）
static void MakeShortcut(string lnkPath, string target, string workDir, string icon)
{
    try
    {
        var dir = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t);
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = workDir;
        lnk.IconLocation = icon + ",0";
        lnk.Description = "NlueMix 蓝牙串流混音器";
        lnk.Save();
    }
    catch { }
}

// 生成卸载脚本（纯 ASCII 的 bat + 中文 ps1）
static void WriteUninstall(string targetDir)
{
    string bat = Path.Combine(targetDir, "卸载.bat");
    var sb = new StringBuilder();
    sb.AppendLine("@echo off");
    sb.AppendLine("chcp 936 >nul");
    sb.AppendLine("echo.");
    sb.AppendLine("echo   正在卸载 NlueMix...");
    sb.AppendLine("taskkill /f /im NlueMix.exe >nul 2>&1");
    sb.AppendLine("timeout /t 2 /nobreak >nul");
    sb.AppendLine("del /f /q \"%USERPROFILE%\\Desktop\\NlueMix.lnk\" >nul 2>&1");
    sb.AppendLine("del /f /q \"%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\NlueMix.lnk\" >nul 2>&1");
    sb.AppendLine("rmdir /s /q \"%LOCALAPPDATA%\\NlueMix\" >nul 2>&1");
    sb.AppendLine("echo   已卸载，窗口会在几秒后自动关闭");
    sb.AppendLine("cd /d \"%TEMP%\"");
    sb.AppendLine("start /b cmd /c \"timeout /t 2 >nul & rmdir /s /q \\\"" + targetDir + "\\\"\"");
    sb.AppendLine("exit /b 0");

    File.WriteAllText(bat, sb.ToString(), Encoding.GetEncoding(936));
}
