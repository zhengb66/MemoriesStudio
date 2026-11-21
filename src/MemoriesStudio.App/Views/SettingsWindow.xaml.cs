using System.IO;
using System.Windows;
using MemoriesStudio.Models;
using MemoriesStudio.Services;
using Microsoft.Win32;

namespace MemoriesStudio.Views;

/// <summary>
/// 全局设置：输出分辨率/帧率 + 背景音乐列表。
/// 音乐直接写入工程 MusicPaths（确定时生效），导出时顺序循环铺满全片。
/// </summary>
public partial class SettingsWindow : Window
{
    public int WidthOut { get; private set; }
    public int HeightOut { get; private set; }
    public int FpsOut { get; private set; }

    private readonly ProjectFile _project;

    /// <summary>音乐列表项：路径 + 探测到的时长（异步回填）。</summary>
    public sealed class MusicEntry : ObservableObject
    {
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        private string _duration = "…";
        public string Duration
        {
            get => _duration;
            set => Set(ref _duration, value);
        }
        /// <summary>探测到的秒数（0=未知/缺失），用于累加背景音乐总时长。</summary>
        public double Seconds { get; set; }
        public MusicEntry(string path) => Path = path;
    }

    public SettingsWindow(ProjectFile project)
    {
        InitializeComponent();
        _project = project;

        CmbRes.ItemsSource = ProjectFile.Presets;
        var idx = ProjectFile.Presets.ToList().FindIndex(p => p.Width == project.Width && p.Height == project.Height);
        CmbRes.SelectedIndex = idx >= 0 ? idx : 0;

        CmbFps.ItemsSource = new[] { 24, 25, 30, 50, 60 };
        CmbFps.SelectedItem = project.Fps is 24 or 25 or 30 or 50 or 60 ? project.Fps : 30;

        WidthOut = project.Width;
        HeightOut = project.Height;
        FpsOut = project.Fps;

        foreach (var p in project.MusicPaths)
        {
            var entry = new MusicEntry(p);
            LstMusic.Items.Add(entry);
            _ = ProbeDurationAsync(entry);
        }
    }

    /// <summary>异步探测单曲时长并回填（LibVLC 解析，失败显示占位符）。</summary>
    private async Task ProbeDurationAsync(MusicEntry entry)
    {
        var path = entry.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            entry.Duration = "缺失";
            UpdateMusicTotal();
            return;
        }
        try
        {
            var probe = await MediaProbe.ProbeAsync(path).ConfigureAwait(true);
            if (probe is { Duration: > 0 })
            {
                entry.Seconds = probe.Duration;
                entry.Duration = FormatClock(probe.Duration);
            }
            else entry.Duration = "—";
        }
        catch
        {
            entry.Duration = "—";
        }
        UpdateMusicTotal();
    }

    /// <summary>重算并设置背景音乐总时长（标题行右侧）。</summary>
    private void UpdateMusicTotal()
    {
        var total = LstMusic.Items.Cast<MusicEntry>().Sum(x => x.Seconds);
        TxtMusicTotal.Text = total > 0 ? $"总时长 {FormatClock(total)}" : "";
    }

    private static string FormatClock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private void OnAddMusic(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择背景音乐（可多选）",
            Multiselect = true,
            Filter = "音频|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.wma|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames)
        {
            if (LstMusic.Items.Cast<MusicEntry>().Any(x =>
                    string.Equals(x.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new MusicEntry(f);
            LstMusic.Items.Add(entry);
            _ = ProbeDurationAsync(entry);
        }
    }

    private void OnRemoveMusic(object sender, RoutedEventArgs e)
    {
        if (LstMusic.SelectedItem is MusicEntry entry)
        {
            LstMusic.Items.Remove(entry);
            UpdateMusicTotal();
        }
    }

    private void OnMoveUpMusic(object sender, RoutedEventArgs e)
    {
        var i = LstMusic.SelectedIndex;
        if (i > 0)
        {
            var v = LstMusic.Items[i]!;
            LstMusic.Items.RemoveAt(i);
            LstMusic.Items.Insert(i - 1, v);
            LstMusic.SelectedIndex = i - 1;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (CmbRes.SelectedItem is ResolutionPreset p)
        {
            WidthOut = p.Width;
            HeightOut = p.Height;
        }
        if (CmbFps.SelectedItem is int f) FpsOut = f;

        // 音乐列表直接写回工程（含缺失文件的路径也保留，便于恢复后继续使用）
        var list = LstMusic.Items.Cast<MusicEntry>().Select(x => x.Path).ToList();
        var changed = list.Count != _project.MusicPaths.Count ||
                      !list.SequenceEqual(_project.MusicPaths, StringComparer.OrdinalIgnoreCase);
        if (changed)
        {
            _project.MusicPaths.Clear();
            foreach (var s in list) _project.MusicPaths.Add(s);
        }
        MusicChanged = changed;

        DialogResult = true;
    }

    /// <summary>音乐列表是否被改动（主窗口据此提示已保存到工程）。</summary>
    public bool MusicChanged { get; private set; }

    /// <summary>自检：返回第一首音乐的时长文本。</summary>
    public string MusicDurationForTest()
        => LstMusic.Items.Count > 0 && LstMusic.Items[0] is MusicEntry e ? e.Duration : "";

    /// <summary>自检：返回背景音乐总时长文本。</summary>
    public string MusicTotalForTest() => TxtMusicTotal.Text;

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
