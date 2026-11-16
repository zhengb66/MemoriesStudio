using System.IO;
using System.Text.Json.Serialization;

namespace MemoriesStudio.Models;

/// <summary>素材库里的一条素材（图片 / 视频 / 音乐）。</summary>
public sealed class MediaAsset : ObservableObject
{
    private string _path = "";
    private MediaKind _kind;
    private int _width;
    private int _height;
    private double _duration;
    private double _fps;
    private bool _hasAudio;
    private string? _thumbnail;

    public string Path { get => _path; set { Set(ref _path, value); Raise(nameof(FileName)); } }
    public MediaKind Kind { get => _kind; set => Set(ref _kind, value); }
    public int Width { get => _width; set { Set(ref _width, value); Raise(nameof(InfoText)); } }
    public int Height { get => _height; set { Set(ref _height, value); Raise(nameof(InfoText)); } }
    public double Duration { get => _duration; set { Set(ref _duration, value); Raise(nameof(InfoText)); } }
    public double Fps { get => _fps; set => Set(ref _fps, value); }
    public bool HasAudio { get => _hasAudio; set => Set(ref _hasAudio, value); }
    public string? ThumbnailPath { get => _thumbnail; set => Set(ref _thumbnail, value); }
    private bool _corrupt;

    /// <summary>导入体检发现文件损坏（解码失败）。导出时会用灰帧替代。</summary>
    [JsonIgnore]
    public bool Corrupt { get => _corrupt; set { Set(ref _corrupt, value); Raise(nameof(InfoText)); } }

    [JsonIgnore] public string FileName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string InfoText => Kind switch
    {
        MediaKind.Image => Corrupt ? $"{Width}×{Height} · ⚠ 文件损坏" : $"{Width}×{Height}",
        // 小于 2 秒的视频（动图）入轨时按图片处理，这里提前给出提示
        MediaKind.Video when Duration > 0.2 && Duration < 2.0 => $"动图 · {Duration:0.0}s（按图片入轨）",
        MediaKind.Video => $"{Width}×{Height} · {Duration:0.0}s",
        MediaKind.Audio => $"{Duration:0.0}s",
        _ => ""
    };

    [JsonIgnore] public bool IsImage => Kind == MediaKind.Image;
    [JsonIgnore] public bool IsVideo => Kind == MediaKind.Video;
    [JsonIgnore] public bool IsAudio => Kind == MediaKind.Audio;
}
