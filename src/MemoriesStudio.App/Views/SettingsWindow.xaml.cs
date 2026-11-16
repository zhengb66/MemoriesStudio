using System.IO;
using System.Windows;
using MemoriesStudio.Models;
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

        foreach (var p in project.MusicPaths) LstMusic.Items.Add(p);
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
            if (!LstMusic.Items.Contains(f))
                LstMusic.Items.Add(f);
    }

    private void OnRemoveMusic(object sender, RoutedEventArgs e)
    {
        if (LstMusic.SelectedItem is string s) LstMusic.Items.Remove(s);
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
        var list = LstMusic.Items.Cast<string>().ToList();
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

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
