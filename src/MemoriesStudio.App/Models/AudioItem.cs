using System.IO;
using System.Text.Json.Serialization;

namespace MemoriesStudio.Models;

/// <summary>
/// 音频轨道上的一条音乐。可以多条并存（片头一首、中段换一首），
/// 每条有自己的起点、时长、音量与循环开关。
/// </summary>
public sealed class AudioItem : ObservableObject
{
    private string _path = "";
    private double _start;
    private double _duration = 30;
    private double _sourceDuration;
    private double _volume = 0.35;
    private bool _loop = true;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Path
    {
        get => _path;
        set { Set(ref _path, value); Raise(nameof(FileName)); }
    }

    /// <summary>在时间轴上的起点（秒）。</summary>
    public double Start
    {
        get => _start;
        set { Set(ref _start, Math.Max(0, value)); Raise(nameof(StartText)); }
    }

    /// <summary>在时间轴上占用的时长（秒）。</summary>
    public double Duration
    {
        get => _duration;
        set { Set(ref _duration, Math.Clamp(value, 0.5, 3600)); Raise(nameof(DurationText)); Raise(nameof(End)); }
    }

    /// <summary>音频文件本身的总时长（探测得到，0 表示未知）。</summary>
    public double SourceDuration
    {
        get => _sourceDuration;
        set { Set(ref _sourceDuration, Math.Max(0, value)); Raise(nameof(MaxDuration)); }
    }

    public double Volume
    {
        get => _volume;
        set { Set(ref _volume, Math.Clamp(value, 0, 1)); Raise(nameof(VolumeText)); }
    }

    public bool Loop
    {
        get => _loop;
        set { Set(ref _loop, value); Raise(nameof(ModeText)); }
    }

    [JsonIgnore] public double End => Start + Duration;
    [JsonIgnore] public string FileName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
    [JsonIgnore] public double MaxDuration => SourceDuration > 0.2 ? SourceDuration : 3600;
    [JsonIgnore] public string StartText => $"{Start:0.0}s";
    [JsonIgnore] public string DurationText => $"{Duration:0.0}s";
    [JsonIgnore] public string VolumeText => $"{Volume * 100:0}%";
    [JsonIgnore] public string ModeText => Loop ? "循环" : "单次";
}
