using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MemoriesStudio.Models;
using MemoriesStudio.Services;

namespace MemoriesStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        // 无界面人脸检测自检：MemoriesStudio.exe --detect <图片路径>
        if (e.Args.Length >= 2 && e.Args[0] == "--detect")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunHeadlessDetect(e.Args[1]);
            return;
        }

        // 无界面批量导出：MemoriesStudio.exe --export <工程.msproj> <输出.mp4> [宽 高 帧率 CRF]
        if (e.Args.Length >= 3 && e.Args[0] == "--export")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunHeadlessExport(e.Args);
            return;
        }

        // 无界面导入自检：MemoriesStudio.exe --import-test <图片路径>
        // 启动主窗口并走真实的导入代码路径，验证模板实例化 / 检测 / 预览渲染，然后自动退出
        if (e.Args.Length >= 2 && e.Args[0] == "--import-test")
        {
            var testPath = e.Args[1];
            var window = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = window;
            window.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.ImportForTest(testPath, e.Args.Length >= 3 ? e.Args[2] : null);
                }
                finally
                {
                    var logPath = Path.Combine(Path.GetTempPath(), "memories_import_test.log");
                    var clipInfo = string.Join("; ",
                        window.Clips.Select(c => $"{Path.GetFileName(c.SourcePath)} dur={c.Duration:0.00} in={c.SourceIn:0.00} src={c.SourceDuration:0.00} {c.SourceWidth}x{c.SourceHeight}" +
                            (c.Kind == Models.MediaKind.Image ? $" face={c.FaceDetected} fh={c.FaceH:0.000} z0={c.StartZoom:0.00} z1={c.EndZoom:0.00} pan={c.PanV} fy0={c.StartFocusY:0.000} fy1={c.EndFocusY:0.000} diag={c.FaceDiag}" : "")));
                    File.WriteAllText(logPath,
                        $"RESULT=OK assets={window.AssetsCount} clips={window.ClipsCount} total={window.TotalDuration:0.00}\r\n{clipInfo}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 无界面预览快照自检：MemoriesStudio.exe --shot-test <素材目录|工程> <时间秒> <输出png>
        // 把预览区真实渲染结果落盘，用于核对竖幅平移的取景几何（黑边 / 裁切）
        if (e.Args.Length >= 4 && e.Args[0] == "--shot-test")
        {
            var srcPath = e.Args[1];
            var atTime = double.Parse(e.Args[2], CultureInfo.InvariantCulture);
            var pngPath = e.Args[3];
            var shotWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = shotWindow;
            shotWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    if (srcPath.EndsWith(".msproj", StringComparison.OrdinalIgnoreCase))
                        await shotWindow.OpenProjectForTest(srcPath);
                    else
                        await shotWindow.ImportForTest(srcPath, null);

                    info = await shotWindow.SnapshotForTest(atTime, pngPath);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_shot.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 时间轴拖动排序自检：MemoriesStudio.exe --reorder-test <工程> <片段序号1起> <目标秒> [截图png]
        // 模拟把第 N 个片段拖到目标时间处，验证顺序变化与总时长不变；第 4 参存落手前整窗截图
        if (e.Args.Length >= 4 && e.Args[0] == "--reorder-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunReorderTest(e.Args[1],
                int.Parse(e.Args[2], CultureInfo.InvariantCulture),
                double.Parse(e.Args[3], CultureInfo.InvariantCulture),
                e.Args.Length >= 5 ? e.Args[4] : null);
            return;
        }

        // 属性面板自检：MemoriesStudio.exe --inspect-test <工程|素材目录> <输出png>
        // 选中第一个视频片段后整窗截图，核对"原视频长度/剪辑后长度"排版
        if (e.Args.Length >= 3 && e.Args[0] == "--inspect-test")
        {
            var srcPath = e.Args[1];
            var pngPath = e.Args[2];
            var insWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = insWindow;
            insWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    if (srcPath.EndsWith(".msproj", StringComparison.OrdinalIgnoreCase))
                        await insWindow.OpenProjectForTest(srcPath);
                    else
                        await insWindow.ImportForTest(srcPath, null);

                    info = insWindow.InspectorForTest(pngPath);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_inspect.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 素材库跟随定位自检：MemoriesStudio.exe --reveal-test <工程> <片段序号1起> <输出png>
        // 选中时间轴指定片段，验证素材库对应素材被滚到视口正中，并落整窗截图
        if (e.Args.Length >= 4 && e.Args[0] == "--reveal-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var rvWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = rvWindow;
            rvWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await rvWindow.OpenProjectForTest(e.Args[1]);
                    await Task.Delay(600);        // 等首帧布局完成，避免用未成形的视口算滚动位置
                    info = rvWindow.RevealForTest(
                        int.Parse(e.Args[2], CultureInfo.InvariantCulture), e.Args[3]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_reveal.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 导入取消选中 + 属性页分组自检：MemoriesStudio.exe --import-sel-test <素材路径> <输出png>
        if (e.Args.Length >= 3 && e.Args[0] == "--import-sel-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var isWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = isWindow;
            isWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    info = await isWindow.ImportSelectionForTest(e.Args[1], e.Args[2]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_importsel.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 人像/景物切换运镜诊断：MemoriesStudio.exe --subject-toggle-test <照片> <输出png>
        if (e.Args.Length >= 3 && e.Args[0] == "--subject-toggle-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var stWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = stWindow;
            stWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    info = await stWindow.SubjectToggleForTest(e.Args[1], e.Args[2]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_subjecttoggle.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 加入时间轴后素材库取消多选自检：MemoriesStudio.exe --add-sel-test <工程> <输出png>
        if (e.Args.Length >= 3 && e.Args[0] == "--add-sel-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var asWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = asWindow;
            asWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    info = await asWindow.AddSelectionForTest(e.Args[1], e.Args[2]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_addsel.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 方向键尾帧定位自检：MemoriesStudio.exe --nav-tail-test <工程> <目标片段序号1起> <输出png>
        // 目标需带前驱转场；验证 → 导航后播放头停在本片段独占区尾帧、预览不显示上一段
        if (e.Args.Length >= 4 && e.Args[0] == "--nav-tail-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var ntWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = ntWindow;
            ntWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await ntWindow.OpenProjectForTest(e.Args[1]);
                    await Task.Delay(600);
                    info = ntWindow.NavTailForTest(
                        int.Parse(e.Args[2], CultureInfo.InvariantCulture), e.Args[3]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_navtail.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 图片属性页自检：MemoriesStudio.exe --face-ui-test <工程> <片段序号1起> <输出png>
        if (e.Args.Length >= 4 && e.Args[0] == "--face-ui-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var fuiWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = fuiWindow;
            fuiWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await fuiWindow.OpenProjectForTest(e.Args[1]);
                    await Task.Delay(400);
                    info = fuiWindow.FaceUiForTest(
                        int.Parse(e.Args[2], CultureInfo.InvariantCulture), e.Args[3]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_faceui.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 图片默认字幕区间自检：MemoriesStudio.exe --img-cap-default-test <工程> <图片片段序号1起> <输出png>
        // 验证图片默认字幕从 1 秒开始、片尾留 1 秒（正好错过首尾转场区）
        if (e.Args.Length >= 4 && e.Args[0] == "--img-cap-default-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var icWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = icWindow;
            icWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await icWindow.OpenProjectForTest(e.Args[1]);
                    await Task.Delay(600);
                    info = icWindow.ImageCaptionDefaultForTest(
                        int.Parse(e.Args[2], CultureInfo.InvariantCulture), e.Args[3]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_imgcapdefault.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 字幕编辑自检：MemoriesStudio.exe --caption-test <工程> <片段序号1起> <片段内秒> <输出png>
        // 验证默认字号 5%、新建字幕起点=播放头在片内位置、选择字幕列表项后播放头定位到区间中心
        if (e.Args.Length >= 5 && e.Args[0] == "--caption-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var capWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = capWindow;
            capWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await capWindow.OpenProjectForTest(e.Args[1]);
                    await Task.Delay(600);
                    info = capWindow.CaptionForTest(
                        int.Parse(e.Args[2], CultureInfo.InvariantCulture),
                        double.Parse(e.Args[3], CultureInfo.InvariantCulture),
                        e.Args[4]);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_caption.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 预览播放探针：MemoriesStudio.exe --play-probe <工程> <秒数> <输出csv>
        if (e.Args.Length >= 4 && e.Args[0] == "--play-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var probeWindow = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = probeWindow;
            probeWindow.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await probeWindow.OpenProjectForTest(e.Args[1]);
                    info = await probeWindow.PlayProbeAsync(
                        double.Parse(e.Args[2], CultureInfo.InvariantCulture), e.Args[3],
                        e.Args.Length >= 5 ? double.Parse(e.Args[4], CultureInfo.InvariantCulture) : 0);
                }
                catch (Exception ex)
                {
                    info = "ERROR " + ex.Message;
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_playprobe.log"),
                        $"RESULT {info}\r\ntime={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 关于对话框自检：MemoriesStudio.exe --about-test <输出目录>
        // 离屏渲染「设置窗口（含左下角关于按钮）」与「关于对话框」两图，便于核对排版
        if (e.Args.Length >= 2 && e.Args[0] == "--about-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var outDir = e.Args[1];
            Dispatcher.InvokeAsync(async () =>
            {
                var log = new StringBuilder();
                try
                {
                    Directory.CreateDirectory(outDir);
                    var settings = new Views.SettingsWindow(new Models.ProjectFile())
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000
                    };
                    settings.Show();
                    settings.UpdateLayout();
                    await Task.Delay(300);
                    var ok1 = SaveWindowPng(settings, Path.Combine(outDir, "settings.png"));
                    log.AppendLine($"settings={(ok1 ? "OK" : "FAIL")} {settings.Width:0}x{settings.ActualHeight:0}");

                    var about = new Views.AboutWindow
                    {
                        Owner = settings,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000
                    };
                    about.Show();
                    about.UpdateLayout();
                    await Task.Delay(300);
                    var ok2 = SaveWindowPng(about, Path.Combine(outDir, "about.png"));
                    log.AppendLine($"about={(ok2 ? "OK" : "FAIL")} {about.Width:0}x{about.ActualHeight:0}");
                    log.AppendLine($"product={ProductInfo.Name} {ProductInfo.Version} author={ProductInfo.Author}");
                    log.AppendLine($"version={ProductInfo.VersionText} copyright={ProductInfo.Copyright}");
                    about.Close();
                    settings.Close();
                }
                catch (Exception ex)
                {
                    log.AppendLine("ERROR " + ex);
                }
                finally
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_about_test.log"),
                        "RESULT=" + (log.ToString().Contains("FAIL") || log.ToString().Contains("ERROR") ? "FAIL" : "OK")
                        + "\r\n" + log + $"time={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 无界面打开工程自检：MemoriesStudio.exe --open-test <工程.msproj>
        if (e.Args.Length >= 2 && e.Args[0] == "--open-test")
        {
            var testPath = e.Args[1];
            var window = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = window;
            window.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.OpenProjectForTest(testPath);
                }
                finally
                {
                    var logPath = Path.Combine(Path.GetTempPath(), "memories_import_test.log");
                    var kinds = string.Join(",", window.AssetsForTest.Select(a => a.Kind + ":" + Path.GetFileName(a.Path)));
                    File.WriteAllText(logPath,
                        $"RESULT=OK assets={window.AssetsCount} clips={window.ClipsCount} total={window.TotalDuration:0.00} " +
                        $"kinds=[{kinds}] time={DateTime.Now:HH:mm:ss}\r\n", Encoding.UTF8);
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        // 剪辑对话框自检：MemoriesStudio.exe --trimtest <视频路径> [秒] [pause] [shot] [in=秒]
        // 打开剪辑窗口播 N 秒，采样播放位置证明"不卡死且画面在走"，然后自动关闭
        // pause = 不自动起播（验证打开后停在起点）；shot = 存界面截图；in=N = 片段入点
        if (e.Args.Length >= 2 && e.Args[0] == "--trimtest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var autoPlay = true;
            var shoot = false;
            var inPoint = 0.0;
            for (var i = 3; i < e.Args.Length; i++)
            {
                var a = e.Args[i];
                if (a == "pause") autoPlay = false;
                else if (a == "shot") shoot = true;
                else if (a.StartsWith("in=", StringComparison.Ordinal))
                    double.TryParse(a.AsSpan(3), NumberStyles.Float, CultureInfo.InvariantCulture, out inPoint);
            }

            RunTrimTest(e.Args[1], e.Args.Length >= 3
                ? double.Parse(e.Args[2], CultureInfo.InvariantCulture) : 4.0, autoPlay, shoot, inPoint);
            return;
        }

        // 无界面播放节奏自检：MemoriesStudio.exe --playtest <视频路径>
        // 模拟 6 秒实时播放，记录播放头位置与实际显示帧时间戳，验证不会"快进/定格"
        if (e.Args.Length >= 2 && e.Args[0] == "--playtest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunHeadlessPlayTest(e.Args[1]);
            return;
        }

        // 无界面探测诊断：MemoriesStudio.exe --probe <素材路径>
        // 打印程序内部探测到的分辨率 / 时长 / 帧率 / 原声，便于排查"显示不正确"类问题
        if (e.Args.Length >= 2 && e.Args[0] == "--probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunHeadlessProbe(e.Args[1]);
            return;
        }

        // 无界面人脸检测诊断：MemoriesStudio.exe --facedebug <图片路径> [offline]
        // offline：禁用网络，只验证「随安装包捆绑的模型」能否独立工作（模拟没联网的新电脑）
        if (e.Args.Length >= 2 && e.Args[0] == "--facedebug")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var offline = e.Args.Length >= 3 &&
                          e.Args[2].Equals("offline", StringComparison.OrdinalIgnoreCase);
            _ = FaceDetector.TryEnsureModelAsync(allowNetwork: !offline).GetAwaiter().GetResult();
            var dump = FaceDetector.DebugDump(e.Args[1]);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_facedebug.log"),
                dump, Encoding.UTF8);
            Shutdown(0);
            return;
        }

        // 无界面抓帧诊断：MemoriesStudio.exe --grabtest <视频路径> [时间] —— 验证拖动预览的单帧抓取
        if (e.Args.Length >= 2 && e.Args[0] == "--grabtest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var log2 = new StringBuilder();
            try
            {
                var time = e.Args.Length >= 3 ? double.Parse(e.Args[2], CultureInfo.InvariantCulture) : 5;
                using var grabber = new Services.VideoGrabber();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = grabber.GrabAsync(e.Args[1], time).GetAwaiter().GetResult();
                var ms1 = sw.ElapsedMilliseconds;
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var data2 = grabber.GrabAsync(e.Args[1], time + 3).GetAwaiter().GetResult();
                var ms2 = sw2.ElapsedMilliseconds;
                log2.AppendLine($"grab1 t={time:0.0}s {ms1}ms bytes={(data?.Length ?? 0)}; " +
                                $"grab2 t={time + 3:0.0}s {ms2}ms bytes={(data2?.Length ?? 0)}; " +
                                $"expect={Services.VideoGrabber.GrabWidth * Services.VideoGrabber.GrabHeight * 4}");
            }
            catch (Exception ex)
            {
                log2.AppendLine("error: " + ex.Message);
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "memories_grabtest.log"),
                log2.ToString(), Encoding.UTF8);
            Shutdown(0);
            return;
        }

        var window2 = new Views.MainWindow();
        MainWindow = window2;
        window2.Show();

        // 文件关联：双击 .msproj（或把工程文件拖到 exe / 快捷方式上）直接打开。
        // 只认第一个非选项参数，且必须存在；其余情况按普通启动处理。
        var openPath = e.Args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("-"));
        if (openPath is not null && File.Exists(openPath)
            && openPath.EndsWith(".msproj", StringComparison.OrdinalIgnoreCase))
        {
            window2.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window2.OpenProjectFromPathAsync(openPath, confirmDiscard: false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "memories_open.log"),
                            $"[{DateTime.Now:HH:mm:ss}] 打开工程失败 {openPath}\r\n{ex}\r\n\r\n", Encoding.UTF8);
                    }
                    catch { }
                    MessageBox.Show("工程打开失败：\n" + ex.Message, "打开失败");
                }
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    private void RunHeadlessPlayTest(string path)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "memories_playtest.log");
        var log = new StringBuilder();
        try
        {
            log.AppendLine($"playtest={path}");
            using var player = new Services.VideoFramePlayer();
            player.Open(path, 0, 30);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var nextMark = 0.5;
            while (sw.Elapsed.TotalSeconds < 6)
            {
                var t = sw.Elapsed.TotalSeconds;
                var frame = player.GetFrame(t);
                if (t >= nextMark)
                {
                    log.AppendLine($"t={t:0.0}s frameTs={player.LastFrameTime:0.00}s " +
                                   $"(offset={player.LastFrameTime - t:+0.00;-0.00}s) frame={(frame is null ? "none" : "ok")}");
                    nextMark += 0.5;
                }
                Thread.Sleep(40);
            }
            // 定格判定：最后 2 秒内画面时间戳是否仍在随播放头推进
            var drifting = Math.Abs(player.LastFrameTime - 6.0) < 1.2;
            log.AppendLine(drifting ? "RESULT=OK (播放节奏与实时同步)" : "RESULT=WARN (显示帧明显偏离播放头)");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(drifting ? 0 : 2);
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=FAIL");
            log.AppendLine(ex.ToString());
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(1);
        }
    }

    /// <summary>退出前释放共享的 LibVLC 实例（否则进程收尾时可能崩溃，退出码非零）。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        MediaProbe.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// 剪辑对话框自检：打开 TrimWindow 播放 seconds 秒，每 300ms 采样一次播放位置。
    /// UI 线程能持续 tick 且位置在推进 = 对话框打开不卡死、libvlc 正在解码。
    /// </summary>
    private void RunTrimTest(string videoPath, double seconds, bool autoPlay = true, bool shoot = false,
        double inPoint = 0)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "memories_trimtest.log");
        var log = new StringBuilder();
        log.AppendLine($"file={videoPath} exists={File.Exists(videoPath)} autoPlay={autoPlay} in={inPoint:0.00}");

        Views.TrimWindow? dlg = null;
        var samples = new List<double>();

        try
        {
            var probe = Services.MediaProbe.ProbeAsync(videoPath).GetAwaiter().GetResult();
            log.AppendLine($"probe={(probe is null ? "null" : $"{probe.Width}x{probe.Height} dur={probe.Duration:0.00} rot={probe.Rotation}")}");

            var clip = new Models.Clip
            {
                SourcePath = videoPath,
                Kind = Models.MediaKind.Video,
                SourceDuration = probe?.Duration ?? 0,
                SourceIn = Math.Clamp(inPoint, 0, Math.Max(0, (probe?.Duration ?? 0) - 1)),
                Duration = 5,
                HasAudio = probe?.HasAudio ?? false
            };
            var maxDur = Math.Max(1, (probe?.Duration ?? 0) - clip.SourceIn);
            clip.Duration = Math.Min(5, maxDur);
            log.AppendLine($"clip in={clip.SourceIn:0.00} dur={clip.Duration:0.00} srcDur={clip.SourceDuration:0.00}");

            dlg = new Views.TrimWindow(clip);
            dlg.Show();

            // 对话框打开后不再自动播放：autoPlay=false 用来验证"停在起点不自己播"
            if (autoPlay)
            {
                dlg.Dispatcher.BeginInvoke(new Action(() => { try { dlg.BeginPlay(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }

            var ticker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var seekTarget = clip.SourceIn + Math.Min(3.0, Math.Max(0.5, clip.Duration - 0.5));
            var beforeSeek = new List<double>();
            var afterSeek = new List<double>();
            bool seeked = false;

            ticker.Tick += (_, _) =>
            {
                if (sw.Elapsed.TotalSeconds < seconds) return;

                ticker.Stop();
                var distinct = samples.Select(s => Math.Round(s, 1)).Distinct().Count();
                bool ok;
                if (autoPlay)
                {
                    // 起播后位置持续推进 = 解码正常
                    ok = distinct >= Math.Min(3, samples.Count);
                }
                else
                {
                    // 不自动播放：初始定位完成后必须停在剪辑起点；再模拟拖动区间条 seek 到目标
                    ok = beforeSeek.Count >= 2 && beforeSeek.All(s => Math.Abs(s - dlg.InPoint) <= 0.6)
                         && afterSeek.Count >= 2
                         && afterSeek.Skip(1).All(s => Math.Abs(s - seekTarget) <= 0.6);
                }
                log.AppendLine("before=" + string.Join(",", beforeSeek.Select(s => s.ToString("0.00", CultureInfo.InvariantCulture))));
                log.AppendLine("after=" + string.Join(",", afterSeek.Select(s => s.ToString("0.00", CultureInfo.InvariantCulture))));
                log.AppendLine($"RESULT={(ok ? "OK" : "FAIL")} ticks={samples.Count} distinct={distinct} " +
                               $"in={dlg.InPoint:0.00} out={dlg.OutPoint:0.00} {dlg.DebugState}");

                // 截图：验证区间条/按钮排版（VideoView 是原生子窗口，只会是黑块）
                if (shoot)
                {
                    try
                    {
                        var w = Math.Max(1, (int)dlg.ActualWidth);
                        var hgt = Math.Max(1, (int)dlg.ActualHeight);
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            w, hgt, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        rtb.Render(dlg);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        var shotPath = Path.Combine(Path.GetTempPath(), "trim_shot.png");
                        using (var fs = File.Create(shotPath)) enc.Save(fs);
                        log.AppendLine($"shot={shotPath} {w}x{hgt}");
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine("shot error: " + ex.Message);
                    }
                }

                try { dlg.Close(); } catch { }
                // 采样结果在 tick 里落盘（方法体 finally 早在计时器启动时就跑过了）
                try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); } catch { }
                Shutdown(0);
            };

            // 采样定时器：暂停模式下在第 1.5 秒模拟拖动区间条到 3.0s
            var sampler = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            sampler.Tick += (_, _) =>
            {
                samples.Add(dlg.CurrentTime);
                if (!autoPlay && !seeked && sw.Elapsed.TotalSeconds >= 2.2 && dlg.IsInitialized)
                {
                    seeked = true;
                    log.AppendLine($"seek to {seekTarget:0.00} (paused)");
                    try { dlg.SeekForTest(seekTarget); } catch (Exception ex) { log.AppendLine("seek error " + ex.Message); }
                    return;
                }
                if (!autoPlay)
                {
                    if (!seeked)
                    {
                        // 只在初始定位完成后采样，避免把"还在打开视频"的 0.00 算进去
                        if (dlg.IsInitialized) beforeSeek.Add(dlg.CurrentTime);
                    }
                    else afterSeek.Add(dlg.CurrentTime);
                }
            };
            sampler.Start();
            ticker.Start();
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=ERROR " + ex);
            try { dlg?.Close(); } catch { }
            Shutdown(1);
        }
        finally
        {
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
        }
    }

    /// <summary>
    /// 时间轴拖动排序自检：打开工程，把第 N 个片段模拟拖到目标时间处，
    /// 用时间轴的插入规则推算期望下标并核对，总时长必须不变。
    /// </summary>
    private void RunReorderTest(string projectPath, int clipNo, double targetTime, string? pngPath)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "memories_reorder.log");
        var log = new StringBuilder();
        try
        {
            var window = new Views.MainWindow { SuppressCloseConfirm = true };
            MainWindow = window;
            window.Show();

            Dispatcher.InvokeAsync(async () =>
            {
                var info = "";
                try
                {
                    await window.OpenProjectForTest(projectPath);

                    var project = window.ProjectForTest ?? throw new InvalidOperationException("工程未打开");
                    var clips = project.Clips;
                    var totalBefore = project.TotalDuration;
                    if (clipNo < 1 || clipNo > clips.Count)
                        throw new ArgumentOutOfRangeException(nameof(clipNo), $"片段序号 {clipNo} 超出范围 1..{clips.Count}");

                    var clip = clips[clipNo - 1];
                    var origIndex = clipNo - 1;

                    // 与 TimelineControl.UpdateReorder 同一规则推算期望插入下标：
                    // 按"拖动块仍占位"的真实屏幕位置，数中点已在鼠标左侧的剩余片段
                    var startsBefore = project.ComputeStarts();
                    var remaining = clips.Where(c => !ReferenceEquals(c, clip)).ToList();
                    var expected = 0;
                    foreach (var c in remaining)
                    {
                        var ci = clips.IndexOf(c);
                        if (startsBefore[ci] + c.Duration * 0.5 < targetTime) expected++;
                    }

                    // 指示线/播放头的期望位置：剩余片段真实分割线（最后一个则到其末尾）
                    double expectedPlayhead;
                    if (remaining.Count == 0) expectedPlayhead = 0;
                    else if (expected < remaining.Count)
                        expectedPlayhead = startsBefore[clips.IndexOf(remaining[expected])];
                    else
                    {
                        var last = remaining[^1];
                        expectedPlayhead = startsBefore[clips.IndexOf(last)] + last.Duration;
                    }
                    expectedPlayhead = Math.Min(expectedPlayhead, totalBefore);

                    // 模拟"长距离拖放"：先把视口滚到目标附近，验证落手后视口不被拽走
                    var sv0 = window.TimelineScroll;
                    var pps = window.Timeline.PixelsPerSecond;
                    sv0.ScrollToHorizontalOffset(Math.Clamp(
                        targetTime * pps - sv0.ViewportWidth * 0.55, 0, Math.Max(0, sv0.ScrollableWidth)));
                    window.UpdateLayout();
                    var offsetBeforeDrop = sv0.HorizontalOffset;

                    var result = window.TimelineForTest.TestReorder(clip, targetTime, () =>
                    {
                        // 落手前截图：核对被拖块压暗与插入指示线
                        if (!string.IsNullOrEmpty(pngPath))
                        {
                            try
                            {
                                window.UpdateLayout();
                                var w = (int)Math.Max(2, window.ActualWidth);
                                var h = (int)Math.Max(2, window.ActualHeight);
                                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(window);
                                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                                using (var fs = File.Create(pngPath)) enc.Save(fs);
                                log.AppendLine($"shot={pngPath}");
                            }
                            catch (Exception ex2) { log.AppendLine("shot error " + ex2.Message); }
                        }
                    });
                    log.AppendLine($"orig={origIndex} target={targetTime:0.00} expected={expected} {result}");

                    // 导出拖动光标位图（图片标志+数量角标），核对图标排版
                    if (!string.IsNullOrEmpty(pngPath))
                    {
                        try
                        {
                            var cursorPng = Path.Combine(
                                Path.GetDirectoryName(pngPath) ?? ".",
                                "drag_cursor.png");
                            var bgra = Controls.TimelineControl.BuildDragCursorBgra(clipNo, false);
                            var src = System.Windows.Media.Imaging.BitmapSource.Create(48, 48, 96, 96,
                                System.Windows.Media.PixelFormats.Bgra32, null, bgra, 48 * 4);
                            var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                            using (var fs = File.Create(cursorPng)) enc2.Save(fs);
                            log.AppendLine($"cursorPng={cursorPng}");
                        }
                        catch (Exception ex3) { log.AppendLine("cursor png error " + ex3.Message); }
                    }

                    var ok = result.StartsWith("OK", StringComparison.Ordinal)
                             && result.Contains($"newIndex={expected}", StringComparison.Ordinal)
                             && Math.Abs(project.TotalDuration - totalBefore) < 0.001
                             && clips.Count == remaining.Count + 1;
                    // 落手后播放头应停在落点（剩余片段的真实分割线，超片尾时钳到总时长）
                    var ph = window.TimelineForTest.Playhead;
                    var phOk = Math.Abs(ph - expectedPlayhead) < 0.35;
                    log.AppendLine($"playhead={ph:0.00} expectedPlayhead={expectedPlayhead:0.00} phOk={phOk}");

                    // 视口保持：落手后偏移应基本不变，且落点确实在当前视野内
                    window.UpdateLayout();
                    var offsetAfterDrop = sv0.HorizontalOffset;
                    var caretX = ph * pps;
                    var caretVisible = caretX >= offsetAfterDrop - 1 && caretX <= offsetAfterDrop + sv0.ViewportWidth + 1;
                    var offsetOk = Math.Abs(offsetAfterDrop - offsetBeforeDrop) < 2;
                    log.AppendLine($"view offsetBefore={offsetBeforeDrop:0.0} after={offsetAfterDrop:0.0} " +
                                   $"viewport={sv0.ViewportWidth:0} caretX={caretX:0.0} " +
                                   $"offsetOk={offsetOk} caretVisible={caretVisible}");

                    // 自动滚动自检：模拟鼠标拖到可视区右/左边缘各执行一次判定
                    var scrollOk = false;
                    try
                    {
                        var sv = window.TimelineScroll;
                        // 归零后再测，避免"已在最右"导致右滚无位移
                        sv.ScrollToHorizontalOffset(0);
                        window.UpdateLayout();
                        var baseOffset = sv.HorizontalOffset;
                        var rightEdge = baseOffset + sv.ViewportWidth - 5;
                        var afterRight = window.TimelineForTest.TestAutoScroll(new Point(rightEdge, 60), out var scrolledRight);
                        var leftEdge = afterRight + 5;
                        var afterLeft = window.TimelineForTest.TestAutoScroll(new Point(leftEdge, 60), out var scrolledLeft);
                        log.AppendLine($"autoscroll right: {baseOffset:0.0}->{afterRight:0.0} scrolled={scrolledRight}; " +
                                       $"left: ->{afterLeft:0.0} scrolled={scrolledLeft}");
                        scrollOk = scrolledRight && scrolledLeft && afterRight > baseOffset && afterLeft < afterRight;
                    }
                    catch (Exception ex4) { log.AppendLine("autoscroll error " + ex4.Message); }

                    log.AppendLine($"RESULT={(ok && phOk && offsetOk && caretVisible && scrollOk ? "OK" : "FAIL")} " +
                                   $"total={project.TotalDuration:0.00} " +
                                   $"order={string.Join(",", clips.Select(c => c.FileName.Substring(0, Math.Min(10, c.FileName.Length))))}");
                }
                catch (Exception ex)
                {
                    log.AppendLine("RESULT=ERROR " + ex.Message);
                }
                finally
                {
                    try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); } catch { }
                    Shutdown(0);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=ERROR " + ex);
            try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); } catch { }
            Shutdown(1);
        }
    }

    private void RunHeadlessProbe(string path)    {
        var logPath = Path.Combine(Path.GetTempPath(), "memories_probe.log");
        var log = new StringBuilder();
        try
        {
            log.AppendLine($"file={path} exists={File.Exists(path)} ffmpeg(仅导出)={FfmpegService.Instance.Available}");
            var r = MediaProbe.ProbeAsync(path).GetAwaiter().GetResult();
            if (r is null)
            {
                log.AppendLine("RESULT=FAIL probe=null (LibVLC+WPF 解码均失败)");
            }
            else
            {
                log.AppendLine($"resolution={r.Width}x{r.Height} duration={r.Duration:0.000}s fps={r.Fps:0.###} " +
                               $"audio={r.HasAudio} vcodec={r.VideoCodec} rotation={r.Rotation}");
                log.AppendLine("RESULT=OK");
            }
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(r is null ? 1 : 0);
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=FAIL");
            log.AppendLine(ex.ToString());
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(1);
        }
    }

    private void RunHeadlessDetect(string imagePath)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "memories_detect.log");
        var log = new StringBuilder();
        try
        {
            var modelTask = FaceDetector.TryEnsureModelAsync();
            modelTask.Wait();
            log.AppendLine($"yunetReady={FaceDetector.YuNetReady} cascadeReady={FaceDetector.ModelReady}");
            log.AppendLine($"yunetModel={FaceDetector.YuNetModelPath ?? "missing"}");
            log.AppendLine($"cascadeModel={FaceDetector.CascadeModelPath ?? "missing"}");

            var detector = new FaceDetector();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = detector.Detect(imagePath);
            sw.Stop();
            log.AppendLine($"focus=({r.X:0.000},{r.Y:0.000}) faceFound={r.FaceFound} count={r.FaceCount} elapsed={sw.ElapsedMilliseconds}ms");
            log.AppendLine("RESULT=OK");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=FAIL");
            log.AppendLine(ex.ToString());
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(1);
        }
    }

    private void RunHeadlessExport(string[] args)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "memories_export.log");
        var log = new StringBuilder();
        try
        {
            var project = ProjectStore.Load(args[1]) ?? throw new InvalidOperationException("工程文件读取失败");
            // 相对路径的素材按工程文件所在目录解析（与主窗口行为一致）
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
            void FixPath(Func<string> get, Action<string> set)
            {
                var v = get();
                if (string.IsNullOrEmpty(v) || Path.IsPathRooted(v)) return;
                var byProject = Path.GetFullPath(Path.Combine(baseDir, v));
                if (File.Exists(byProject)) set(byProject);
                else set(Path.GetFullPath(v));
            }
            foreach (var c in project.Clips) FixPath(() => c.SourcePath, v => c.SourcePath = v);
            for (var i = 0; i < project.MusicPaths.Count; i++)
            {
                var idx = i;
                FixPath(() => project.MusicPaths[idx], v => project.MusicPaths[idx] = v);
            }
            project.MigrateLegacyMusic();
            var outPath = args[2];
            var w = args.Length > 3 ? int.Parse(args[3]) : 1280;
            var h = args.Length > 4 ? int.Parse(args[4]) : 720;
            var fps = args.Length > 5 ? int.Parse(args[5]) : 30;
            var crf = args.Length > 6 ? int.Parse(args[6]) : 20;

            log.AppendLine($"project={args[1]} clips={project.Clips.Count} total={project.TotalDuration:0.00}s");
            log.AppendLine($"output={outPath} {w}x{h}@{fps} crf={crf}");

            var engine = new ExportEngine();
            var plan = engine.BuildPlan(project, new ExportOptions(outPath, w, h, fps, crf, false));
            log.AppendLine("--- filter graph ---");
            log.AppendLine(plan.FilterScript);
            log.AppendLine("--------------------");

            var progress = new Progress<ExportProgress>(p => log.AppendLine($"{p.Percent:0.0}% {p.Status}"));
            engine.ExportAsync(project, new ExportOptions(outPath, w, h, fps, crf, false), progress, CancellationToken.None)
                  .GetAwaiter().GetResult();

            log.AppendLine("RESULT=OK");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT=FAIL");
            log.AppendLine(ex.ToString());
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            Shutdown(1);
        }
    }

    private static int _errorCount;
    private static string? _lastErrorKey;

    /// <summary>把窗口内容离屏渲染成 PNG（自检用，不依赖截屏工具）。</summary>
    private static bool SaveWindowPng(Window w, string pngPath)
    {
        try
        {
            if (w.Content is not FrameworkElement root) return false;
            var width = (int)Math.Ceiling(root.ActualWidth);
            var height = (int)Math.Ceiling(root.ActualHeight);
            if (width <= 0 || height <= 0) return false;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(pngPath);
            enc.Save(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 完整异常（含内层异常与堆栈）追加到日志文件，便于排查
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "memories_error.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss}] {e.Exception}\r\n\r\n", Encoding.UTF8);
        }
        catch { }

        var inner = e.Exception;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var key = inner.Message;

        // 同样的错误只弹一次，避免弹窗风暴；窗口关闭由用户决定
        _errorCount++;
        if (_errorCount > 50)
        {
            e.Handled = true;
            return;
        }
        if (key != _lastErrorKey)
        {
            _lastErrorKey = key;
            MessageBox.Show(key + $"\r\n\r\n（第 {_errorCount} 次异常，详细日志见 %TEMP%\\memories_error.log）",
                "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }
}
