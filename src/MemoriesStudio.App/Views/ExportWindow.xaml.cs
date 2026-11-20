using System.Diagnostics;
using System.IO;
using System.Windows;
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

        // 分辨率/帧率统一取工程设置（「设置」对话框），导出窗口只读展示，不再单独提供下拉
        TxtResFps.Text = $"{project.Width}×{project.Height} · {project.Fps}fps  （在「设置」中修改）";

        CmbQuality.ItemsSource = new[] { "高画质（推荐）", "标准", "小体积" };
        CmbQuality.SelectedIndex = 0;

        // 统一软编码：AVC 兼容广，HEVC 同画质体积更小。
        // 默认按分辨率：720p/1080p（≤1920）用 AVC，2K/4K 用 HEVC。
        CmbFormat.ItemsSource = new[] { "AVC（H.264，兼容性好）", "HEVC（H.265，体积更小）" };
        CmbFormat.SelectedIndex = ExportOptions.DefaultCodecFor(project.Width) == VideoCodec.Hevc ? 1 : 0;

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MemoriesStudio");
        Directory.CreateDirectory(dir);
        var name = string.IsNullOrWhiteSpace(project.Name) ? "我的记忆" : project.Name;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        TxtPath.Text = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmm}.mp4");

        CmbFormat.SelectionChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    /// <summary>当前选中的编码格式（列表顺序与 VideoCodec 枚举一致：0=AVC，1=HEVC）。</summary>
    private VideoCodec SelectedCodec => CmbFormat.SelectedIndex == 1 ? VideoCodec.Hevc : VideoCodec.Avc;

    private void UpdateSummary()
    {
        var total = _project.TotalDuration;
        var frames = (long)Math.Round(total * _project.Fps);
        TxtSummary.Text = $"共 {_project.Clips.Count} 个片段 · 时长 {total:0.0} 秒 · " +
                          $"{_project.Width}×{_project.Height} · {_project.Fps}fps · {ExportOptions.DisplayName(SelectedCodec)} · 约 {frames} 帧";
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
        var codec = SelectedCodec;
        var codecName = codec == VideoCodec.Hevc ? "libx265" : "libx264";

        var opt = new ExportOptions(path, _project.Width, _project.Height, _project.Fps, crf, codec);
        var plan = _engine.BuildPlan(_project, opt);
        Append($"输出：{path}");
        Append($"时长：{plan.TotalDuration:0.0}s　片段：{_project.Clips.Count}　编码：{ExportOptions.DisplayName(codec)}（{codecName}）　CRF：{crf}");
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

    /// <summary>自检：导出窗口的分辨率显示、编码格式与是否还保留了独立下拉/硬编码选项。</summary>
    public string DescribeForTest()
        => $"resfps={TxtResFps.Text} codec={SelectedCodec} default={ExportOptions.DefaultCodecFor(_project.Width)} " +
           $"hasCmbEncoder={(FindName("CmbEncoder") is not null)} hasCmbRes={(FindName("CmbRes") is not null)} " +
           $"proj={_project.Width}x{_project.Height}@{_project.Fps}";

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
