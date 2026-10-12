using System.Diagnostics;

namespace NlueMix.Setup;

/// <summary>
/// 安装界面。
///
/// 刻意的设计取舍：
///   · 只有【一个】按钮。安装程序不该让用户做选择 ——
///     所有能自动做完的事（放文件、建快捷方式、开防火墙）都自动做掉。
///   · 安装位置固定在自己的 LocalAppData，不提供"浏览"。理由见下方注释。
///   · 全程把每一步写出来给用户看，不搞转圈圈 —— 用户能看见"防火墙已放行"
///     比看见一个进度条更有安全感。
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly Button _install = new();
    private readonly CheckBox _launch = new();
    private readonly TextBox _log = new();
    private readonly Label _path = new();

    public SetupForm()
    {
        Text = "NlueMix 安装程序";
        ClientSize = new Size(560, 440);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(0xFA, 0xFB, 0xFD);

        var title = new Label
        {
            Text = "NlueMix",
            Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x10, 0x18, 0x28),
            Location = new Point(24, 20),
            AutoSize = true,
        };

        var sub = new Label
        {
            Text = "把手机的声音，通过局域网送到这台电脑的蓝牙音响。",
            ForeColor = Color.FromArgb(0x5A, 0x64, 0x78),
            Location = new Point(27, 62),
            AutoSize = true,
        };

        var pathLabel = new Label
        {
            Text = "安装位置：",
            ForeColor = Color.FromArgb(0x5A, 0x64, 0x78),
            Location = new Point(27, 100),
            AutoSize = true,
        };

        _path.Text = Program.InstallDir;
        _path.ForeColor = Color.FromArgb(0x2F, 0x6B, 0xE0);
        _path.Location = new Point(100, 100);
        _path.AutoSize = true;

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Location = new Point(27, 130);
        _log.Size = new Size(506, 220);
        _log.BackColor = Color.White;
        _log.ForeColor = Color.FromArgb(0x10, 0x18, 0x28);
        _log.Font = new Font("Cascadia Mono, Consolas, Microsoft YaHei UI", 8.5f);
        _log.BorderStyle = BorderStyle.FixedSingle;

        _launch.Text = "安装完成后启动 NlueMix";
        _launch.Checked = true;
        _launch.Location = new Point(27, 362);
        _launch.AutoSize = true;
        _launch.ForeColor = Color.FromArgb(0x10, 0x18, 0x28);

        _install.Text = "开始安装";
        _install.Size = new Size(150, 38);
        _install.Location = new Point(383, 384);
        _install.FlatStyle = FlatStyle.Flat;
        _install.FlatAppearance.BorderSize = 0;
        _install.BackColor = Color.FromArgb(0x2F, 0x6B, 0xE0);
        _install.ForeColor = Color.White;
        _install.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        _install.Cursor = Cursors.Hand;
        _install.Click += (_, _) => RunInstall();

        Controls.AddRange(new Control[] { title, sub, pathLabel, _path, _log, _launch, _install });
    }

    private void Log(string text)
    {
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
        Application.DoEvents();
    }

    /// <summary>装完之后那颗按钮就变成"关闭"，用这个标记区分两种行为。</summary>
    private bool _done;

    private void RunInstall()
    {
        if (_done)
        {
            if (_launch.Checked) TryStartApp();
            Close();
            return;
        }

        _install.Enabled = false;
        _launch.Enabled = false;

        try
        {
            Log("1) 释放程序文件…");
            Program.ExtractPayload(Log);

            Log("");
            Log("2) 创建快捷方式…");
            Program.CreateShortcut(Program.DesktopLnk, "NlueMix —— 手机声音送到电脑音响");
            Log("   桌面：" + Program.DesktopLnk);
            Program.CreateShortcut(Program.StartMenuLnk, "NlueMix");
            Log("   开始菜单：" + Program.StartMenuLnk);

            Log("");
            Log("3) 放行 Windows 防火墙…");
            Log("   （这步很关键：不放行的话手机连不上，而电脑端日志里连一条连接尝试都不会有）");
            var fw = Program.ApplyFirewall();
            Log(string.IsNullOrWhiteSpace(fw) ? "   已放行 TCP 47820 / UDP 47821（覆盖全部网络配置）" : fw);

            Log("");
            Log("安装完成。");

            _done = true;
            _install.Text = "完成并关闭";
            _install.Enabled = true;
            _launch.Enabled = true;
        }
        catch (Exception ex)
        {
            Log("");
            Log("安装失败：" + ex.Message);
            _install.Text = "重试";
            _install.Enabled = true;
            _launch.Enabled = true;
        }
    }

    private void TryStartApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Program.ExePath,
                WorkingDirectory = Program.InstallDir,
                UseShellExecute = true,
            });
            Log("已启动 NlueMix。");
        }
        catch (Exception ex)
        {
            Log("启动失败（可以稍后从桌面快捷方式打开）：" + ex.Message);
        }
    }
}
