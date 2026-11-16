using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MemoriesStudio.Setup;

/// <summary>安装向导：单窗口，安装路径 + 两个选项 + 进度 + 完成页。</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color BgDark = Color.FromArgb(0x22, 0x25, 0x2E);
    private static readonly Color Accent = Color.FromArgb(0xE8, 0xA3, 0x3D);
    private static readonly Color Muted = Color.FromArgb(0x8A, 0x90, 0xA0);

    private readonly TextBox _txtDir = new();
    private readonly Button _btnBrowse = new();
    private readonly CheckBox _chkDesktop = new();
    private readonly CheckBox _chkAssoc = new();
    private readonly CheckBox _chkRun = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _lblStatus = new();
    private readonly Button _btnPrimary = new();
    private readonly Button _btnClose = new();
    private readonly Button _btnDefaultApps = new();
    private readonly Label _lblHint = new();

    private bool _installing;
    private bool _done;
    private bool _finishing;
    private string _logPath = "";

    public SetupForm()
    {
        Text = AppInfo.DisplayName + " 安装程序";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(620, 442);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        BuildHeader();
        BuildBody();
        Load += (_, _) => { };
    }

    private void BuildHeader()
    {
        var header = new Panel { Bounds = new Rectangle(0, 0, 620, 96), BackColor = BgDark };
        Controls.Add(header);

        header.Controls.Add(new Label
        {
            Text = AppInfo.DisplayName,
            Font = new Font("Microsoft YaHei UI", 21f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(28, 20)
        });
        header.Controls.Add(new Label
        {
            Text = "把照片与视频做成带运镜与转场的影片    版本 " + AppInfo.DisplayVersion,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(0xA8, 0xAE, 0xBF),
            AutoSize = true,
            Location = new Point(31, 62)
        });
        header.Controls.Add(new Panel { Bounds = new Rectangle(0, 94, 620, 2), BackColor = Accent });
    }

    private void BuildBody()
    {
        Controls.Add(new Label
        {
            Text = "安装位置",
            Location = new Point(28, 116),
            AutoSize = true,
            ForeColor = Color.FromArgb(0x33, 0x36, 0x40)
        });

        _txtDir.Text = AppInfo.InstalledDir() ?? AppInfo.DefaultInstallDir;
        _txtDir.Bounds = new Rectangle(28, 140, 456, 26);
        _txtDir.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtDir);

        _btnBrowse.Text = "浏览…";
        _btnBrowse.Bounds = new Rectangle(492, 139, 100, 28);
        _btnBrowse.Click += (_, _) => PickFolder();
        Controls.Add(_btnBrowse);

        _lblHint.Text = "自带 ffmpeg、LibVLC 与 .NET 运行库，无需另外安装。约需 750 MB 空闲空间。";
        _lblHint.Bounds = new Rectangle(28, 174, 564, 20);
        _lblHint.ForeColor = Muted;
        Controls.Add(_lblHint);

        _chkDesktop.Text = "创建桌面快捷方式";
        _chkDesktop.Checked = true;
        _chkDesktop.Bounds = new Rectangle(28, 200, 300, 24);
        Controls.Add(_chkDesktop);

        _chkAssoc.Text = "把 .msproj 工程文件与 Memories Studio 关联（双击直接打开工程）";
        _chkAssoc.Checked = true;
        _chkAssoc.Bounds = new Rectangle(28, 226, 564, 24);
        Controls.Add(_chkAssoc);

        Controls.Add(new Panel
        {
            Bounds = new Rectangle(28, 258, 564, 1),
            BackColor = Color.FromArgb(0xE2, 0xE4, 0xEA)
        });

        _lblStatus.Text = "准备就绪。点击「安装」开始。";
        _lblStatus.Bounds = new Rectangle(28, 270, 564, 20);
        _lblStatus.ForeColor = Color.FromArgb(0x44, 0x48, 0x54);
        Controls.Add(_lblStatus);

        _bar.Bounds = new Rectangle(28, 294, 564, 14);
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        _bar.Style = ProgressBarStyle.Continuous;
        Controls.Add(_bar);

        _chkRun.Text = "安装完成后立即启动 Memories Studio";
        _chkRun.Checked = true;
        _chkRun.Bounds = new Rectangle(28, 322, 400, 24);
        _chkRun.Visible = false;
        Controls.Add(_chkRun);

        _btnDefaultApps.Text = "打开「默认应用」设置";
        _btnDefaultApps.Bounds = new Rectangle(190, 374, 210, 36);
        _btnDefaultApps.Visible = false;
        _btnDefaultApps.Click += (_, _) => OpenDefaultApps();
        Controls.Add(_btnDefaultApps);

        _btnPrimary.Text = "安装";
        _btnPrimary.Bounds = new Rectangle(408, 374, 98, 36);
        _btnPrimary.BackColor = Accent;
        _btnPrimary.ForeColor = Color.FromArgb(0x1A, 0x1B, 0x21);
        _btnPrimary.FlatStyle = FlatStyle.Flat;
        _btnPrimary.FlatAppearance.BorderSize = 0;
        _btnPrimary.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        _btnPrimary.Click += (_, _) => OnPrimary();
        Controls.Add(_btnPrimary);

        _btnClose.Text = "退出";
        _btnClose.Bounds = new Rectangle(514, 374, 78, 36);
        _btnClose.FlatStyle = FlatStyle.Flat;
        _btnClose.Click += (_, _) => Close();
        Controls.Add(_btnClose);

        FormClosing += OnFormClosing;
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择 Memories Studio 的安装目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_txtDir.Text) ? _txtDir.Text : AppInfo.DefaultInstallDir
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtDir.Text = dlg.SelectedPath;
    }

    private void OnPrimary()
    {
        if (_done) { FinishAndMaybeLaunch(); return; }
        if (_installing) return;
        _ = StartInstallAsync();
    }

    private async Task StartInstallAsync()
    {
        var dir = _txtDir.Text.Trim();
        if (dir.Length == 0) { MessageBox.Show(this, "请选择安装目录。", Text); return; }

        try { dir = Path.GetFullPath(dir); }
        catch { MessageBox.Show(this, "安装目录无效。", Text); return; }

        if (File.Exists(Path.Combine(dir, AppInfo.ExeName)) && AppInfo.InstalledDir() is { } old &&
            string.Equals(old.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var r = MessageBox.Show(this,
                "该目录已安装 Memories Studio，继续将覆盖升级（工程文件与素材不受影响）。是否继续？",
                "升级安装", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
        }

        // 程序正在运行会锁住 exe / dll，先结束
        if (Installer.KillRunning(dir))
            SetupLog.Write("已结束正在运行的程序");

        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "无法写入该目录：\n" + dir + "\n\n" + ex.Message +
                "\n\n建议使用默认目录，或选择自己有写权限的位置。",
                "安装目录不可写", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installing = true;
        SetBusy(true);
        _bar.Value = 0;
        _lblStatus.Text = "正在准备…";

        var desktop = _chkDesktop.Checked;
        var assoc = _chkAssoc.Checked;
        var progress = new Progress<(string Msg, double Pct)>(p =>
        {
            _lblStatus.Text = p.Msg;
            _bar.Value = Math.Clamp((int)Math.Round(p.Pct * 100), 0, 100);
        });

        var log = new StringBuilder();
        _logPath = Path.Combine(Path.GetTempPath(), "memories_setup.log");
        try { File.WriteAllText(_logPath, $"install dir={dir} desktop={desktop} assoc={assoc} {DateTime.Now}\r\n"); } catch { }

        try
        {
            await Task.Run(() => Installer.Install(dir, desktop, assoc,
                (msg, pct) => ((IProgress<(string, double)>)progress).Report((msg, pct)),
                s => { lock (log) log.AppendLine(s); }));
        }
        catch (Exception ex)
        {
            _installing = false;
            SetBusy(false);
            _bar.Value = 0;
            _lblStatus.Text = "安装失败：" + ex.Message;
            try { File.AppendAllText(_logPath, ex.ToString()); } catch { }
            MessageBox.Show(this,
                "安装失败：\n" + ex.Message + "\n\n详细信息见：\n" + _logPath,
                "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try { File.AppendAllText(_logPath, log.ToString()); } catch { }

        _installing = false;
        _done = true;
        _bar.Value = 100;
        ApplyInstalledState(assoc);
    }

    /// <summary>安装完成后的界面状态：解除忙碌、把「安装」换成可点的「完成」。</summary>
    private void ApplyInstalledState(bool assoc)
    {
        // 关键：这里必须先解除忙碌状态，否则「完成」按钮仍是禁用的，点不动也退不出
        SetBusy(false);

        // 装完后安装路径与选项不再允许改动，避免误以为还能换目录重装
        _txtDir.Enabled = false;
        _btnBrowse.Enabled = false;
        _chkDesktop.Enabled = false;
        _chkAssoc.Enabled = false;

        _lblStatus.Text = "安装完成。";
        _lblStatus.ForeColor = Color.FromArgb(0x1F, 0x7A, 0x3D);

        _btnPrimary.Text = "完成";
        _btnPrimary.Enabled = true;
        _btnClose.Visible = false;
        _chkRun.Visible = true;
        _chkRun.Enabled = true;
        _btnDefaultApps.Visible = true;
        _btnDefaultApps.Enabled = true;
        _lblHint.Text = assoc
            ? "已注册 .msproj 关联。若双击工程仍不是本程序打开，请点下方按钮到「默认应用」里选择 Memories Studio。"
            : "未注册 .msproj 关联，可随时重新运行安装程序勾选。";
        _lblHint.ForeColor = Muted;
    }

    private void SetBusy(bool busy)
    {
        _txtDir.Enabled = !busy;
        _btnBrowse.Enabled = !busy;
        _chkDesktop.Enabled = !busy;
        _chkAssoc.Enabled = !busy;
        _btnPrimary.Enabled = !busy;
        _btnClose.Enabled = !busy;
        _btnDefaultApps.Enabled = !busy;
        if (busy) _btnPrimary.Text = "正在安装…";
    }

    private void FinishAndMaybeLaunch()
    {
        if (_finishing) return;
        _finishing = true;
        _btnPrimary.Enabled = false;

        var exe = Path.Combine(_txtDir.Text.Trim(), AppInfo.ExeName);
        var launched = false;
        if (_chkRun.Checked && File.Exists(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                });
                launched = true;
            }
            catch (Exception ex) { SetupLog.Write("启动失败 " + ex.Message); }
        }

        SetupLog.Write($"安装程序收尾 launched={launched}");

        // 收起窗口并结束消息循环；Application.Exit 兜底，确保安装程序进程一定退出
        try { Hide(); } catch { }
        try { Close(); } catch (Exception ex) { SetupLog.Write("关闭窗口异常 " + ex.Message); }
        try { Application.Exit(); } catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_installing) return;
        e.Cancel = true;
        MessageBox.Show(this, "正在安装，请等待完成。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenDefaultApps()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "无法打开设置：" + ex.Message, Text); }
    }

    // ---------------------------------------------------------------- 自检钩子（--uitest）

    internal bool PrimaryEnabledForTest => _btnPrimary.Enabled;
    internal string PrimaryTextForTest => _btnPrimary.Text;
    internal bool CloseButtonVisibleForTest => _btnClose.Visible;
    internal bool RunCheckUsableForTest => _chkRun.Visible && _chkRun.Enabled;
    internal bool PathEditableForTest => _txtDir.Enabled;

    /// <summary>自检：直接把界面切到「安装完成」状态（不真的安装）。</summary>
    internal void SimulateInstalledForTest(bool assoc = true)
    {
        _done = true;
        _bar.Value = 100;
        ApplyInstalledState(assoc);
    }

    internal void SetLaunchAfterForTest(bool v) => _chkRun.Checked = v;

    internal void ClickPrimaryForTest() => OnPrimary();
}
