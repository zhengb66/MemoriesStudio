using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MemoriesStudio.Setup;

/// <summary>应用与安装信息常量。</summary>
internal static class AppInfo
{
    public const string DisplayName = "Memories Studio";
    public const string DisplayVersion = "0.7.3";
    public const string Publisher = "Memories Studio";
    public const string ExeName = "MemoriesStudio.exe";
    public const string UninstallName = "uninstall.exe";
    public const string Ext = ".msproj";
    public const string ProgId = "MemoriesStudio.Project";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MemoriesStudio";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MemoriesStudio");

    public static string StartMenuLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Memories Studio.lnk");

    public static string DesktopLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Memories Studio.lnk");

    /// <summary>已安装目录（读注册表），没有则 null。</summary>
    public static string? InstalledDir()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey);
            var dir = k?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        }
        catch { }
        return null;
    }
}

/// <summary>安装 / 卸载的实际动作。</summary>
internal static class Installer
{
    private const string PayloadResource = "payload.zip";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>通知资源管理器刷新图标 / 关联缓存。</summary>
    public static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0 /*SHCNF_IDLIST*/, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    // ---------------------------------------------------------------- 安装

    /// <summary>
    /// 解压内置载荷到安装目录，创建快捷方式并写注册表。
    /// progress: (状态文字, 0~1)；message: 详细日志行。
    /// </summary>
    public static void Install(string targetDir, bool desktopShortcut, bool associate,
        Action<string, double>? progress = null, Action<string>? message = null)
    {
        void Msg(string s) => message?.Invoke(s);
        void Prog(string s, double p) => progress?.Invoke(s, p);

        targetDir = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);

        // 1) 解压载荷（约 500MB，逐条流式解压，带进度）
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException(
                "安装包损坏：未找到内置载荷。请重新下载完整的安装程序。");

        // 单文件发布时资源流可能不可随机访问，先落到临时文件再按 zip 打开
        Stream zipStream = res;
        string? tempZip = null;
        if (!res.CanSeek)
        {
            tempZip = Path.Combine(Path.GetTempPath(), "ms_payload_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                res.CopyTo(fs);
            res.Dispose();
            zipStream = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        }

        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            ExtractAll(zip, targetDir, progress, message);
        }
        finally
        {
            zipStream.Dispose();
            if (tempZip is not null)
            {
                try { File.Delete(tempZip); } catch { }
            }
        }

        Prog("正在配置快捷方式…", 0.88);
        CopyUninstaller(targetDir, Msg);
        CreateShortcuts(targetDir, desktopShortcut, Msg);

        Prog("正在注册文件关联…", 0.94);
        if (associate) RegisterAssociation(targetDir, Msg);
        else UnregisterAssociation(Msg);

        Prog("正在写入卸载信息…", 0.97);
        WriteUninstallEntry(targetDir);

        WriteVersionFile(targetDir);
        NotifyShell();
        Prog("安装完成", 1.0);
    }

    /// <summary>把 zip 内所有条目解压到目标目录（逐块复制，按字节推进度）。</summary>
    private static void ExtractAll(ZipArchive zip, string targetDir,
        Action<string, double>? progress, Action<string>? message)
    {
        var root = targetDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var entries = zip.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
        var totalBytes = Math.Max(1L, entries.Sum(x => x.Length));
        long doneBytes = 0;
        var lastPct = -1;

        message?.Invoke($"解压 {entries.Count} 个文件到 {targetDir}");
        foreach (var entry in entries)
        {
            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(targetDir, rel));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包内容非法：" + entry.FullName);

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            // 覆盖旧文件：先清只读，避免升级时删不掉
            if (File.Exists(dest))
            {
                try
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                    File.Delete(dest);
                }
                catch { }
            }

            using (var src = entry.Open())
            using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    doneBytes += n;
                    var pct = (int)(doneBytes * 100 / totalBytes);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progress?.Invoke($"正在安装：{entry.Name}  ({pct}%)",
                            0.05 + 0.80 * doneBytes / totalBytes);
                    }
                }
            }

            message?.Invoke("  解压 " + rel);
        }
    }

    private static void WriteVersionFile(string dir)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, "version.txt"),
                $"{AppInfo.DisplayName} {AppInfo.DisplayVersion}\r\n安装于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n安装目录 {dir}\r\n");
        }
        catch { }
    }

    /// <summary>把安装程序自身复制为 uninstall.exe。</summary>
    private static void CopyUninstaller(string targetDir, Action<string> msg)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return;
            var dest = Path.Combine(targetDir, AppInfo.UninstallName);
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return;

            File.Copy(self, dest, true);
            msg("卸载程序 → " + dest);
        }
        catch (Exception ex)
        {
            msg("复制卸载程序失败（可忽略）: " + ex.Message);
        }
    }

    private static void CreateShortcuts(string targetDir, bool desktop, Action<string> msg)
    {
        var exe = Path.Combine(targetDir, AppInfo.ExeName);
        var ok = ShellLink.Create(AppInfo.StartMenuLink, exe, null, targetDir, exe, 0,
            AppInfo.DisplayName + " — 把照片与视频做成影片");
        msg((ok ? "开始菜单快捷方式 OK" : "开始菜单快捷方式失败") + "：" + AppInfo.StartMenuLink);

        if (!desktop) return;
        var ok2 = ShellLink.Create(AppInfo.DesktopLink, exe, null, targetDir, exe, 0,
            AppInfo.DisplayName);
        msg((ok2 ? "桌面快捷方式 OK" : "桌面快捷方式失败") + "：" + AppInfo.DesktopLink);
    }

    // ---------------------------------------------------------------- 文件关联

    /// <summary>注册 .msproj → Memories Studio（HKCU，无需管理员）。</summary>
    public static void RegisterAssociation(string targetDir, Action<string>? msg = null)
    {
        var exe = Path.Combine(targetDir, AppInfo.ExeName);

        using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + AppInfo.Ext))
        {
            ext.SetValue(null, AppInfo.ProgId);
            ext.SetValue("Content Type", "application/x-memories-studio-project");
            using var owp = ext.CreateSubKey("OpenWithProgids");
            owp.SetValue(AppInfo.ProgId, Array.Empty<byte>(), RegistryValueKind.Binary);
        }

        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + AppInfo.ProgId))
        {
            prog.SetValue(null, "Memories Studio 工程文件");
            using (var icon = prog.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        // 让本应用出现在「设置 → 默认应用」里
        using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\MemoriesStudio\Capabilities"))
        {
            cap.SetValue("ApplicationName", AppInfo.DisplayName);
            cap.SetValue("ApplicationDescription", "把照片与视频做成带运镜与转场的影片");
            cap.SetValue("ApplicationIcon", $"\"{exe}\",0");
            using var fa = cap.CreateSubKey("FileAssociations");
            fa.SetValue(AppInfo.Ext, AppInfo.ProgId);
        }
        using (var ra = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            ra.SetValue(AppInfo.DisplayName, @"Software\MemoriesStudio\Capabilities");

        msg?.Invoke($"{AppInfo.Ext} → {AppInfo.ProgId}");
    }

    /// <summary>移除文件关联（只清理属于自己的部分）。</summary>
    public static void UnregisterAssociation(Action<string>? msg = null)
    {
        try
        {
            using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + AppInfo.Ext, true))
            {
                if (ext is not null)
                {
                    try { ext.DeleteSubKeyTree("OpenWithProgids", false); } catch { }
                    // 只有当默认值仍指向我们时才整体删除，避免破坏其他程序设置的关联
                    if (string.Equals(ext.GetValue(null) as string, AppInfo.ProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        ext.Close();
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + AppInfo.Ext, false);
                    }
                }
            }
        }
        catch { }

        foreach (var path in new[]
                 {
                     @"Software\Classes\" + AppInfo.ProgId,
                     @"Software\MemoriesStudio",
                 })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { }
        }

        try
        {
            using var ra = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true);
            ra?.DeleteValue(AppInfo.DisplayName, false);
        }
        catch { }

        msg?.Invoke("已移除文件关联");
        NotifyShell();
    }

    // ---------------------------------------------------------------- 卸载信息

    public static void WriteUninstallEntry(string targetDir)
    {
        var exe = Path.Combine(targetDir, AppInfo.ExeName);
        var uninst = Path.Combine(targetDir, AppInfo.UninstallName);
        long sizeKb = 0;
        try
        {
            sizeKb = new DirectoryInfo(targetDir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length) / 1024;
        }
        catch { }

        using var k = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKey);
        k.SetValue("DisplayName", AppInfo.DisplayName);
        k.SetValue("DisplayVersion", AppInfo.DisplayVersion);
        k.SetValue("DisplayIcon", exe + ",0");
        k.SetValue("Publisher", AppInfo.Publisher);
        k.SetValue("InstallLocation", targetDir);
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("UninstallString", $"\"{uninst}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{uninst}\" --uninstall --quiet");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (sizeKb > 0) k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
    }

    // ---------------------------------------------------------------- 卸载

    /// <summary>结束正在运行的程序（卸载 / 覆盖安装前调用）。</summary>
    public static bool KillRunning(string? targetDir = null)
    {
        var killed = false;
        try
        {
            foreach (var p in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(AppInfo.ExeName)))
            {
                try
                {
                    if (targetDir is not null)
                    {
                        var file = p.MainModule?.FileName;
                        if (file is not null &&
                            !string.Equals(Path.GetDirectoryName(file), targetDir, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    p.Kill(true);
                    p.WaitForExit(3000);
                    killed = true;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return killed;
    }

    /// <summary>
    /// 清理快捷方式 / 注册表，然后交给批处理延迟删除安装目录
    /// （uninstall.exe 自身就在目录内，进程活着时无法删除）。
    /// </summary>
    public static void Uninstall(string targetDir, Action<string>? msg = null)
    {
        targetDir = Path.GetFullPath(targetDir);
        KillRunning(targetDir);

        foreach (var link in new[] { AppInfo.StartMenuLink, AppInfo.DesktopLink })
        {
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        }

        UnregisterAssociation(msg);
        try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false); } catch { }

        ScheduleDeleteDirectory(targetDir, msg);
    }

    private static void ScheduleDeleteDirectory(string dir, Action<string>? msg)
    {
        var bat = Path.Combine(Path.GetTempPath(), $"ms_uninstall_{Guid.NewGuid():N}.bat");
        // 说明：uninstall.exe 自己就在目录里，必须等本进程退出后才能删；
        // 而杀软扫描 / 刚拷贝的大文件可能短时间占住句柄，所以用重试循环而不是一次性 rmdir。
        var text =
            "@echo off\r\n" +
            "setlocal enabledelayedexpansion\r\n" +
            "set N=0\r\n" +
            ":retry\r\n" +
            "ping 127.0.0.1 -n 2 >nul\r\n" +
            $"rmdir /s /q \"{dir}\" 2>nul\r\n" +
            $"if not exist \"{dir}\" goto done\r\n" +
            "set /a N+=1\r\n" +
            "if !N! lss 20 goto retry\r\n" +
            ":done\r\n" +
            "del /f /q \"%~f0\"\r\n";
        try
        {
            File.WriteAllText(bat, text, System.Text.Encoding.Default);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + bat + "\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            msg?.Invoke("已安排删除安装目录：" + dir);
        }
        catch (Exception ex)
        {
            msg?.Invoke("删除安装目录失败: " + ex.Message);
        }
    }
}
