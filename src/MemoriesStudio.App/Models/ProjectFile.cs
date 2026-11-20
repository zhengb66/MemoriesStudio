using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MemoriesStudio.Models;

public sealed class ResolutionPreset
{
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public override string ToString() => $"{Name}  ({Width}×{Height})";
}

public sealed class ProjectFile : ObservableObject
{
    private string _name = "未命名项目";
    private int _width = 1920;
    private int _height = 1080;
    private int _fps = 30;
    private string? _musicPath;
    private double _musicVolume = 0.35;
    private bool _musicLoop = true;
    private double _musicFadeIn = 1.0;
    private double _musicFadeOut = 2.0;
    private bool _musicDuckOnVideo = true;
    private int _crf = 18;
    private bool _useHardware = false;

    public string Name { get => _name; set => Set(ref _name, value); }
    public int Width { get => _width; set { Set(ref _width, value); Raise(nameof(AspectRatio)); } }
    public int Height { get => _height; set { Set(ref _height, value); Raise(nameof(AspectRatio)); } }
    public int Fps { get => _fps; set => Set(ref _fps, Math.Clamp(value, 12, 60)); }

    private ObservableCollection<Clip> _clips = new();
    public ObservableCollection<Clip> Clips
    {
        get => _clips;
        set
        {
            if (ReferenceEquals(_clips, value)) return;
            UnhookClips(_clips);
            _clips = value ?? new();
            HookClips(_clips);
            _starts = null;
        }
    }

    /// <summary>
    /// 背景音乐文件列表（全局设置里维护）。导出时按顺序循环铺满整条时间轴：
    /// 只在显示照片的区间出声，音乐进出各做淡入/淡出。预览不播配乐。
    /// </summary>
    public List<string> MusicPaths { get; set; } = new();

    /// <summary>旧版工程的音频轨字段，仅用于打开旧工程时把路径迁移进 MusicPaths。</summary>
    public ObservableCollection<AudioItem> AudioItems { get; set; } = new();

    /// <summary>旧版工程的单曲配乐字段，仅用于打开旧工程时迁移。</summary>
    public string? MusicPath { get => _musicPath; set { Set(ref _musicPath, value); Raise(nameof(MusicFileName)); } }
    public double MusicVolume { get => _musicVolume; set { Set(ref _musicVolume, Math.Clamp(value, 0, 1)); } }
    public bool MusicLoop { get => _musicLoop; set => Set(ref _musicLoop, value); }
    public double MusicFadeIn { get => _musicFadeIn; set => Set(ref _musicFadeIn, Math.Max(0, value)); }
    public double MusicFadeOut { get => _musicFadeOut; set => Set(ref _musicFadeOut, Math.Max(0, value)); }

    /// <summary>0.2 的旧选项；0.3 起配乐一律只在照片段播放，字段仅为旧工程反序列化兼容。</summary>
    public bool MusicDuckOnVideo { get => _musicDuckOnVideo; set => Set(ref _musicDuckOnVideo, value); }

    /// <summary>0=无损画质 51=最差；默认 18。</summary>
    public int Crf { get => _crf; set => Set(ref _crf, Math.Clamp(value, 0, 40)); }
    public bool UseHardware { get => _useHardware; set => Set(ref _useHardware, value); }

    [JsonIgnore] public double AspectRatio => Height == 0 ? 16.0 / 9 : (double)Width / Height;
    [JsonIgnore] public string MusicFileName => MusicPaths.Count > 0
        ? System.IO.Path.GetFileName(MusicPaths[0]) + (MusicPaths.Count > 1 ? $" 等 {MusicPaths.Count} 首" : "")
        : "（无）";

    /// <summary>
    /// 旧工程迁移：把旧版单曲 MusicPath 与音频轨 AudioItems 的路径并入 MusicPaths
    /// （去重、按现有顺序），随后清空旧字段，保存时工程即为新格式。
    /// </summary>
    public void MigrateLegacyMusic()
    {
        var paths = new List<string>(MusicPaths);
        if (!string.IsNullOrEmpty(MusicPath) && System.IO.File.Exists(MusicPath))
            paths.Add(MusicPath);
        foreach (var a in AudioItems)
            if (!string.IsNullOrEmpty(a.Path) && System.IO.File.Exists(a.Path))
                paths.Add(a.Path);

        MusicPaths.Clear();
        foreach (var p in paths)
            if (!MusicPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                MusicPaths.Add(p);

        MusicPath = null;
        AudioItems.Clear();
    }

    public static IReadOnlyList<ResolutionPreset> Presets { get; } = new[]
    {
        new ResolutionPreset { Name = "高清 HD", Width = 1920, Height = 1080 },
        new ResolutionPreset { Name = "2K QHD", Width = 2560, Height = 1440 },
        new ResolutionPreset { Name = "4K UHD", Width = 3840, Height = 2160 },
        new ResolutionPreset { Name = "720p", Width = 1280, Height = 720 },
    };

    // ---------------- 时间轴计算 ----------------

    // ComputeStarts 在播放/绘制/命中测试热路径中单帧被调用 8~12 次，每次 new 数组 + O(n)。
    // 这里缓存结果，靠集合/片段属性变更自动失效；12 个调用点无需改动。
    [JsonIgnore] private double[]? _starts;
    [JsonIgnore] private NotifyCollectionChangedEventHandler? _clipsChangedHandler;
    [JsonIgnore] private PropertyChangedEventHandler? _clipPropertyHandler;

    public ProjectFile()
    {
        HookClips(_clips);
    }

    private void HookClips(ObservableCollection<Clip> clips)
    {
        _clipsChangedHandler ??= OnClipsChanged;
        _clipPropertyHandler ??= OnClipPropertyChanged;
        clips.CollectionChanged += _clipsChangedHandler;
        foreach (var c in clips) c.PropertyChanged += _clipPropertyHandler;
    }

    private void UnhookClips(ObservableCollection<Clip> clips)
    {
        if (_clipsChangedHandler is not null) clips.CollectionChanged -= _clipsChangedHandler;
        if (_clipPropertyHandler is not null)
            foreach (var c in clips) c.PropertyChanged -= _clipPropertyHandler;
    }

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _starts = null;
        if (_clipPropertyHandler is null) return;
        if (e.NewItems is not null)
            foreach (Clip c in e.NewItems) c.PropertyChanged += _clipPropertyHandler;
        if (e.OldItems is not null)
            foreach (Clip c in e.OldItems) c.PropertyChanged -= _clipPropertyHandler;
        if (e.Action == NotifyCollectionChangedAction.Reset && sender is ObservableCollection<Clip> col)
            foreach (var c in col) c.PropertyChanged += _clipPropertyHandler;
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只有影响起点计算的属性才失效：Duration 影响本段与下一段，Transition/TransitionDuration
        // 影响本段与上一段的重叠。其余（人脸、运镜、字幕）不改变时间轴布局。
        if (e.PropertyName is nameof(Clip.Duration) or nameof(Clip.Transition) or nameof(Clip.TransitionDuration))
            _starts = null;
    }

    /// <summary>每个片段在成片时间轴上的起点（考虑转场重叠）。结果缓存，变更自动失效。</summary>
    public double[] ComputeStarts()
    {
        if (_starts is not null) return _starts;
        var starts = new double[Clips.Count];
        for (var i = 0; i < Clips.Count; i++)
        {
            starts[i] = i == 0
                ? 0
                : starts[i - 1] + Clips[i - 1].Duration - Clips[i].EffectiveTransition(Clips[i - 1]);
        }
        _starts = starts;
        return starts;
    }

    public double TotalDuration
    {
        get
        {
            if (Clips.Count == 0) return 0;
            var starts = ComputeStarts();
            return starts[^1] + Clips[^1].Duration;
        }
    }

    /// <summary>定位时间点所在的片段与片段内时间。</summary>
    public (int Index, double LocalTime) Locate(double time)
    {
        if (Clips.Count == 0) return (-1, 0);
        var starts = ComputeStarts();
        for (var i = starts.Length - 1; i >= 0; i--)
        {
            if (time >= starts[i] - 1e-6)
                return (i, Math.Clamp(time - starts[i], 0, Clips[i].Duration));
        }
        return (0, 0);
    }
}
