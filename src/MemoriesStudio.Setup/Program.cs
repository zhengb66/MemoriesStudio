using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MemoriesStudio.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var first = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        // 卸载：由 uninstall.exe --uninstall 调用（也支持控制面板静默卸载）
        if (first is "--uninstall" or "/uninstall")
            return RunUninstall(args);

        // 静默安装（自检 / 批量部署）：--install <目录> [--no-desktop] [--no-assoc]
        if (first is "--install" or "/install")
            return RunSilentInstall(args);

        // 安装包自检：--selftest [报告路径]
        if (first is "--selftest" or "/selftest")
            return RunSelfTest(args.Length >= 2 ? args[1] : null);

        // 界面自检：--uitest [报告路径]（验证安装完成后「完成」可点、点击即退出）
        if (first is "--uitest" or "/uitest")
            return RunUiTest(args.Length >= 2 ? args[1] : null);

        // 完成页截图：--uishot <png>（把界面切到「安装完成」状态后渲染成图片）
        if (first is "--uishot" or "/uishot")
            return RunUiShot(args.Length >= 2 ? args[1] : null);

        // 双击 uninstall.exe（无参数）
        var self = Environment.ProcessPath ?? "";
        if (string.Equals(Path.GetFileName(self), AppInfo.UninstallName, StringComparison.OrdinalIgnoreCase))
        {
            var dir = AppInfo.InstalledDir() ?? Path.GetDirectoryName(self) ?? "";
            var r = MessageBox.Show(
                $"确定要卸载 {AppInfo.DisplayName} 吗？\r\n\r\n安装目录：{dir}",
                "卸载 " + AppInfo.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return 0;
            Installer.Uninstall(dir, s => SetupLog.Write(s));
            return 0;
        }

        using var form = new SetupForm();
        Application.Run(form);
        return 0;
    }

    // ---------------------------------------------------------------- 卸载

    private static int RunUninstall(string[] args)
    {
        var dir = ArgValue(args, "--dir") ?? AppInfo.InstalledDir()
                  ?? Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var quiet = args.Any(a => a is "--quiet" or "/quiet");

        SetupLog.Write($"uninstall dir={dir} quiet={quiet}");

        if (!quiet)
        {
            var r = MessageBox.Show(
                $"确定要卸载 {AppInfo.DisplayName} 吗？\r\n\r\n安装目录：{dir}\r\n\r\n" +
                "工程文件和素材不会被删除。",
                "卸载 " + AppInfo.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return 0;
        }

        Installer.Uninstall(dir, s => SetupLog.Write("  " + s));
        SetupLog.Write("卸载完成");
        if (!quiet)
            MessageBox.Show("卸载完成。工程文件与素材未受影响。",
                AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    // ---------------------------------------------------------------- 静默安装

    private static int RunSilentInstall(string[] args)
    {
        var dir = ArgValue(args, "--dir") ?? AppInfo.DefaultInstallDir;
        var desktop = !args.Any(a => a is "--no-desktop");
        var assoc = !args.Any(a => a is "--no-assoc");
        var log = new StringBuilder();

        try
        {
            Installer.KillRunning(null);
            Installer.Install(dir, desktop, assoc,
                progress: null,
                message: s => log.AppendLine(s));
            SetupLog.Write($"silent install ok dir={dir}\r\n{log}");
            return 0;
        }
        catch (Exception ex)
        {
            SetupLog.Write("silent install FAILED\r\n" + ex + "\r\n" + log);
            return 1;
        }
    }

    // ---------------------------------------------------------------- 自检

    /// <summary>
    /// 全链路自检：安装到临时目录 → 校验文件 / 注册表 / 快捷方式 / ffmpeg 可执行
    /// → 用 uninstall.exe 卸载 → 校验注册表与目录清理。结果写 %TEMP%\memories_setup_test.log。
    /// </summary>
    private static int RunSelfTest(string? reportPath)
    {
        var report = new StringBuilder();
        var dir = Path.Combine(Path.GetTempPath(), "ms_setup_selftest");
        var ok = true;

        void Line(string s) { report.AppendLine(s); SetupLog.Write("selftest: " + s); }
        void Check(string name, bool pass, string detail = "")
        {
            if (!pass) ok = false;
            Line($"{(pass ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        try
        {
            Line($"=== Memories Studio 安装包自检 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Line($"payload 版本 {AppInfo.DisplayVersion}  目标目录 {dir}");

            // 0) 清场
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }

            // 1) 安装
            var sw = Stopwatch.StartNew();
            var msgs = new StringBuilder();
            Installer.Install(dir, desktopShortcut: true, associate: true,
                progress: null, message: s => msgs.AppendLine(s));
            Line($"安装耗时 {sw.Elapsed.TotalSeconds:0.0}s");
            Line("--- 安装日志 ---");
            Line(msgs.ToString().TrimEnd());

            // 2) 关键文件
            foreach (var rel in new[]
                     {
                         AppInfo.ExeName,
                         @"libvlc\win-x64\libvlc.dll",
                         @"libvlc\win-x64\libvlccore.dll",
                         @"models\face_detection_yunet_2023mar.onnx",
                         @"ffmpeg\ffmpeg.exe",
                         AppInfo.UninstallName,
                         "version.txt"
                     })
            {
                var p = Path.Combine(dir, rel);
                var fi = File.Exists(p) ? new FileInfo(p) : null;
                Check("文件 " + rel, fi is not null, fi is null ? "缺失" : $"{fi.Length / 1048576.0:0.0} MB");
            }

            Check("libvlc/plugins 目录", Directory.Exists(Path.Combine(dir, "libvlc", "win-x64", "plugins")),
                Directory.Exists(Path.Combine(dir, "libvlc", "win-x64", "plugins")) ? "" : "缺失");

            // 3) 注册表
            using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.msproj"))
                Check("注册表 .msproj → ProgId", (ext?.GetValue(null) as string) == AppInfo.ProgId,
                    ext?.GetValue(null) as string ?? "缺失");

            using (var cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + AppInfo.ProgId + @"\shell\open\command"))
            {
                var v = cmd?.GetValue(null) as string ?? "";
                Check("注册表 打开命令", v.Contains(AppInfo.ExeName) && v.Contains("%1"), v);
            }

            using (var icon = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + AppInfo.ProgId + @"\DefaultIcon"))
                Check("注册表 默认图标", (icon?.GetValue(null) as string ?? "").Contains("MemoriesStudio.exe"));

            using (var un = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey))
            {
                var loc = un?.GetValue("InstallLocation") as string ?? "";
                var unstr = un?.GetValue("UninstallString") as string ?? "";
                Check("卸载项 InstallLocation", string.Equals(loc.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), loc);
                Check("卸载项 UninstallString", unstr.Contains("uninstall.exe") && unstr.Contains("--uninstall"), unstr);
            }

            using (var ra = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
                Check("默认应用注册", ra?.GetValue(AppInfo.DisplayName) is not null);

            // 4) 快捷方式
            Check("开始菜单快捷方式", File.Exists(AppInfo.StartMenuLink), AppInfo.StartMenuLink);
            Check("桌面快捷方式", File.Exists(AppInfo.DesktopLink), AppInfo.DesktopLink);

            // 5) 内置 ffmpeg 可执行
            var ffDir = Path.Combine(dir, "ffmpeg");
            var ff = Path.Combine(ffDir, "ffmpeg.exe");
            if (File.Exists(ff))
            {
                var psi = new ProcessStartInfo(ff)
                {
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                var firstLine = outp.Split('\n').FirstOrDefault()?.Trim() ?? "";
                Check("内置 ffmpeg 可执行", p.HasExited && p.ExitCode == 0 && outp.Contains("ffmpeg version"),
                    firstLine.Length > 90 ? firstLine[..90] : firstLine);
            }

            // 6) 主程序能被系统加载（用 -version 类自检入口）
            var exe = Path.Combine(dir, AppInfo.ExeName);
            if (File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe)
                {
                    Arguments = "--probe \"" + Path.Combine(dir, "version.txt") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                var exited = p.WaitForExit(30000);
                if (!exited) { try { p.Kill(true); } catch { } }
                Check("主程序可启动", exited, exited ? "已加载并退出" : "30s 未退出（已结束）");
            }

            // 7) 卸载
            var uninst = Path.Combine(dir, AppInfo.UninstallName);
            Check("卸载程序存在", File.Exists(uninst));
            if (File.Exists(uninst))
            {
                var psi = new ProcessStartInfo(uninst) { Arguments = "--uninstall --quiet", UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var exited = p.WaitForExit(20000);
                Check("卸载进程完成", exited, exited ? $"exit={p.ExitCode}" : "超时");
            }

            // 批处理删除目录是带重试的（最长约 40s），这里给足等待时间
            for (var i = 0; i < 90 && Directory.Exists(dir); i++) Thread.Sleep(500);
            Check("安装目录已删除", !Directory.Exists(dir), dir);

            using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey))
                Check("卸载项已移除", k is null);
            using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.msproj"))
                Check("关联已移除", ext is null || (ext.GetValue(null) as string) != AppInfo.ProgId);
            Check("开始菜单快捷方式已移除", !File.Exists(AppInfo.StartMenuLink));
            Check("桌面快捷方式已移除", !File.Exists(AppInfo.DesktopLink));
        }
        catch (Exception ex)
        {
            ok = false;
            Line("EXCEPTION " + ex);
        }

        Line($"RESULT={(ok ? "OK" : "FAIL")}");
        report.AppendLine($"time={DateTime.Now:HH:mm:ss}");

        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "memories_setup_test.log");
        try { File.WriteAllText(path, report.ToString(), Encoding.UTF8); } catch { }
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- 界面自检

    /// <summary>
    /// 界面自检（不真的安装）：把窗口切到「安装完成」状态 →
    /// 校验「完成」按钮可点、选项可操作 → 模拟点击「完成」→ 校验窗口关闭、消息循环退出。
    /// </summary>
    private static int RunUiTest(string? reportPath)
    {
        var report = new StringBuilder();
        var ok = true;

        void Check(string name, bool pass, string detail = "")
        {
            if (!pass) ok = false;
            report.AppendLine($"{(pass ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        report.AppendLine($"=== 安装程序界面自检 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

        var form = new SetupForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        form.Location = new Point(-4000, -4000);   // 挪出屏幕，避免闪一下

        form.Shown += (_, _) =>
        {
            try
            {
                form.SetLaunchAfterForTest(false);   // 自检不启动主程序
                form.SimulateInstalledForTest(assoc: true);

                Check("完成按钮可点", form.PrimaryEnabledForTest);
                Check("完成按钮文字", form.PrimaryTextForTest == "完成", form.PrimaryTextForTest);
                Check("退出按钮已隐藏", !form.CloseButtonVisibleForTest);
                Check("立即启动选项可用", form.RunCheckUsableForTest);
                Check("安装路径已锁定", !form.PathEditableForTest);

                form.ClickPrimaryForTest();
                var sw = Stopwatch.StartNew();
                while (form.Visible && sw.ElapsedMilliseconds < 3000)
                {
                    Application.DoEvents();
                    Thread.Sleep(30);
                }
                Check("点完成后窗口关闭", !form.Visible, form.Visible ? "3s 后仍可见" : "");
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("EXCEPTION " + ex);
            }
            finally
            {
                try { if (!form.IsDisposed) form.Close(); } catch { }
                Application.ExitThread();
            }
        };

        Application.Run(form);
        report.AppendLine($"RESULT={(ok ? "OK" : "FAIL")}");
        report.AppendLine($"time={DateTime.Now:HH:mm:ss}");

        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "memories_setup_uitest.log");
        try { File.WriteAllText(path, report.ToString(), Encoding.UTF8); } catch { }
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- 完成页截图

    /// <summary>把界面切到「安装完成」状态后渲染成 PNG，便于核对排版与按钮状态。</summary>
    private static int RunUiShot(string? png)
    {
        var form = new SetupForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        form.Location = new Point(-4000, -4000);

        form.Shown += (_, _) =>
        {
            try
            {
                form.SimulateInstalledForTest(assoc: true);
                form.Refresh();
                Application.DoEvents();

                var outPath = png ?? Path.Combine(Path.GetTempPath(), "memories_setup_ui.png");
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                SetupLog.Write("完成页截图 → " + outPath);
            }
            catch (Exception ex) { SetupLog.Write("截图失败 " + ex); }
            finally { Application.ExitThread(); }
        };

        Application.Run(form);
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

/// <summary>安装器日志（%TEMP%\memories_setup.log）。</summary>
internal static class SetupLog
{
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "memories_setup.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n", Encoding.UTF8);
            }
        }
        catch { }
    }
}
