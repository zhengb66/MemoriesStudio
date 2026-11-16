using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MemoriesStudio.Models;
using MemoriesStudio.Services;
using Microsoft.Win32;

namespace MemoriesStudio.Views;

public partial class ExportWindow : Window
{
    private readonly ProjectFile _project;
    private readonly ExportEngine _engine = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _outputPath;

    public ExportWindow(ProjectFile project)
    {
        InitializeComponent();
        _project = project;

        CmbRes.ItemsSource = ProjectFile.Presets;
        CmbRes.DisplayMemberPath = "Name";
        CmbRes.SelectedIndex = ProjectFile.Presets.ToList()
            .FindIndex(p => p.Width == project.Width && p.Height == project.Height) is var i && i >= 0 ? i : 0;

        CmbFps.ItemsSource = new[] { 24, 25, 30, 50, 60 };
        CmbFps.SelectedItem = project.Fps is 24 or 25 or 30 or 50 or 60 ? project.Fps : 30;

        CmbQuality.ItemsSource = new[] { "高画质（推荐）", "标准", "小体积" };
        CmbQuality.SelectedIndex = 0;

        // 实测（RTX 3070 Ti，1080p 运动内容 cq/crf16）：NVENC 保留 84% 细节、
        // libx264 保留 99% —— 硬编在运动画面上明显偏软，默认走 CPU 画质优先。
        CmbEncoder.ItemsSource = new[] { "CPU（画质优先，推荐）", "GPU 加速（更快，清晰度略低）" };
        CmbEncoder.SelectedIndex = 0;

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MemoriesStudio");
        Directory.CreateDirectory(dir);
        var name = string.IsNullOrWhiteSpace(project.Name) ? "我的记忆" : project.Name;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        TxtPath.Text = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmm}.mp4");

        CmbRes.SelectionChanged += (_, _) => UpdateSummary();
        CmbFps.SelectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private ResolutionPreset Preset => CmbRes.SelectedItem as ResolutionPreset ?? ProjectFile.Presets[0];
    private int Fps => CmbFps.SelectedItem is int f ? f : 30;

    private void UpdateSummary()
    {
        var total = _project.TotalDuration;
        var frames = (long)Math.Round(total * Fps);
        TxtSummary.Text = $"共 {_project.Clips.Count} 个片段 · 时长 {total:0.0} 秒 · " +
                          $"{Preset.Width}×{Preset.Height} · {Fps}fps · 约 {frames} 帧";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "MP4 视频|*.mp4",
            FileName = Path.GetFileName(TxtPath.Text),
            InitialDirectory = Path.GetDirectoryName(TxtPath.Text)
        };
        if (dlg.ShowDialog(this) == true) TxtPath.Text = dlg.FileName;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var path = TxtPath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "请先选择保存位置。", "提示");
            return;
        }
        if (_project.Clips.Count == 0)
        {
            MessageBox.Show(this, "时间轴里还没有片段。", "提示");
            return;
        }

        var crf = CmbQuality.SelectedIndex switch { 0 => 16, 1 => 20, _ => 24 };
        var hw = CmbEncoder.SelectedIndex == 1;

        if (hw)
        {
            var enc = await FfmpegService.Instance.DetectHardwareEncoderAsync();
            hw = enc is not null;
            if (!hw) Append("未检测到 NVENC/QSV/AMF，自动改用 CPU 编码（libx264）。");
        }

        var opt = new ExportOptions(path, Preset.Width, Preset.Height, Fps, crf, hw);
        var plan = _engine.BuildPlan(_project, opt);
        Append($"输出：{path}");
        Append($"时长：{plan.TotalDuration:0.0}s　片段：{_project.Clips.Count}　编码器：{(hw ? "h264_nvenc" : "libx264")}　CRF：{crf}");
        Append("");

        _running = true;
        _cts = new CancellationTokenSource();
        BtnStart.IsEnabled = false;
        BtnClose.Content = "取消";
        Bar.Value = 0;
        TxtProgress.Text = "开始渲染…";

        var progress = new Progress<ExportProgress>(p =>
        {
            Bar.Value = p.Percent;
            TxtProgress.Text = p.Status;
        });

        try
        {
            await Task.Run(() => _engine.ExportAsync(_project, opt, progress, _cts.Token));
            Bar.Value = 100;
            TxtProgress.Text = "导出完成";
            _outputPath = path;
            BtnOpenFolder.Visibility = Visibility.Visible;
            Append("");
            Append("✅ 导出成功。");
        }
        catch (OperationCanceledException)
        {
            TxtProgress.Text = "已取消";
            Append("已取消导出。");
        }
        catch (Exception ex)
        {
            TxtProgress.Text = "导出失败";
            Append("❌ " + ex.Message);
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            BtnStart.IsEnabled = true;
            BtnClose.Content = "关闭";
        }
    }

    private void Append(string line)
    {
        TxtLog.Text += line + "\n";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_outputPath is null || !File.Exists(_outputPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_outputPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            var r = MessageBox.Show(this, "正在导出，确定要中断吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cts?.Cancel();
        }
        base.OnClosing(e);
    }
}
