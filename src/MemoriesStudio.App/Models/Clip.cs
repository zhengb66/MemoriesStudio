using System.IO;
using System.Text.Json.Serialization;

namespace MemoriesStudio.Models;

/// <summary>时间轴上的一个片段（一张照片或一段视频）。</summary>
public sealed class Clip : ObservableObject
{
    private string _sourcePath = "";
    private MediaKind _kind = MediaKind.Image;
    private double _duration = 4.0;
    private double _sourceIn;
    private double _sourceDuration;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _hasAudio;
    private TransitionKind _transition = TransitionKind.CrossFade;
    private double _transitionDuration = 1.0;
    private MotionStyle _motion = MotionStyle.Auto;

    private double _startZoom = 1.0;
    private double _endZoom = 1.15;
    private double _startFocusX = 0.5, _startFocusY = 0.5;
    private double _endFocusX = 0.5, _endFocusY = 0.5;
    private int _panV;

    private double _faceX = 0.5, _faceY = 0.5, _faceSy = 0.5;
    private double _faceH;
    private int _faceHv;
    private bool _faceDetected;
    private string? _thumbnailPath;

    public Guid Id { get; set; } = Guid.NewGuid();

    private List<Caption> _captions = new();
    /// <summary>
    /// 片段内的硬字幕（图片通常一段、视频可多段）。随片段保存与删除，无独立字幕轨；
    /// 时间为片段局部秒，拖动片段顺序时字幕自然跟随。
    /// </summary>
    public List<Caption> Captions { get => _captions; set { Set(ref _captions, value); Raise(nameof(HasCaptions)); } }

    [JsonIgnore] public bool HasCaptions => Captions.Any(c => c.IsValid);

    public string SourcePath
    {
        get => _sourcePath;
        set { Set(ref _sourcePath, value); Raise(nameof(FileName)); }
    }

    public MediaKind Kind { get => _kind; set { Set(ref _kind, value); Raise(nameof(IsImage)); Raise(nameof(IsVideo)); } }

    /// <summary>
    /// 动图（小于 2 秒的视频）按图片入轨时，SourcePath 指向"在视频 1/2 处截的那一帧"缓存 JPG，
    /// 这里存原始视频路径：缓存被清理后可重新截帧，工程仍能正常打开。
    /// 普通照片/视频片段为空。
    /// </summary>
    public string? StillFrom { get; set; }

    /// <summary>该片段在成片中的显示时长（秒）。缩短时把字幕区间收回界内。</summary>
    public double Duration
    {
        get => _duration;
        set
        {
            Set(ref _duration, Math.Clamp(value, 0.3, 3600));
            foreach (var c in _captions) c.ClampTo(_duration);
            Raise(nameof(DurationText));
        }
    }

    /// <summary>视频素材的入点（秒）。</summary>
    public double SourceIn { get => _sourceIn; set => Set(ref _sourceIn, Math.Max(0, value)); }

    public double SourceDuration
    {
        get => _sourceDuration;
        set { Set(ref _sourceDuration, value); Raise(nameof(MaxDuration)); Raise(nameof(DurationText)); }
    }

    public int SourceWidth { get => _sourceWidth; set => Set(ref _sourceWidth, value); }
    public int SourceHeight { get => _sourceHeight; set => Set(ref _sourceHeight, value); }

    /// <summary>视频自带音轨。它为 true 时导出会把原声与原样保留地混入。</summary>
    public bool HasAudio { get => _hasAudio; set => Set(ref _hasAudio, value); }

    public TransitionKind Transition
    {
        get => _transition;
        set { Set(ref _transition, value); Raise(nameof(TransitionText)); }
    }

    public double TransitionDuration
    {
        get => _transitionDuration;
        set { Set(ref _transitionDuration, Math.Clamp(value, 0.1, 5)); Raise(nameof(TransitionText)); }
    }

    public MotionStyle Motion
    {
        get => _motion;
        set { Set(ref _motion, value); Raise(nameof(MotionText)); }
    }

    // ---------- 运镜参数（归一化焦点 + 起止缩放） ----------
    public double StartZoom { get => _startZoom; set => Set(ref _startZoom, Math.Clamp(value, 1.0, 4.0)); }
    public double EndZoom { get => _endZoom; set => Set(ref _endZoom, Math.Clamp(value, 1.0, 4.0)); }
    public double StartFocusX { get => _startFocusX; set => Set(ref _startFocusX, Math.Clamp(value, -1, 2)); }
    public double StartFocusY { get => _startFocusY; set => Set(ref _startFocusY, Math.Clamp(value, -2, 3)); }
    public double EndFocusX { get => _endFocusX; set => Set(ref _endFocusX, Math.Clamp(value, -1, 2)); }
    public double EndFocusY { get => _endFocusY; set => Set(ref _endFocusY, Math.Clamp(value, -2, 3)); }

    /// <summary>
    /// 竖幅照片的纵向平移标记：1 = 画面在竖幅原图上从上到下（或下到上）滑动，末尾停在人脸所在横带。
    /// 竖幅照片按画布比例 cover 裁切后上下余量很大，只做缩放会变成静止画面，必须靠平移体现运镜。
    /// 0 = 普通缩放/焦点运镜（横向素材）。
    /// </summary>
    public int PanV { get => _panV; set => Set(ref _panV, value); }

    /// <summary>检测到的人脸中心（归一化），供自动运镜使用。</summary>
    public double FaceX { get => _faceX; set => Set(ref _faceX, value); }
    public double FaceY { get => _faceY; set => Set(ref _faceY, value); }

    /// <summary>
    /// 人脸中心在原图上的归一化 Y（0=图片顶部，1=图片底部）。
    /// 竖幅照片按画布比例裁切后，人脸常落在可见横带之外，画布坐标会被钳成 0/1 而失效，
    /// 纵向平移必须用这个原图坐标才能正确取景。
    /// </summary>
    public double FaceSY { get => _faceSy; set => Set(ref _faceSy, value); }

    /// <summary>画布归一化头高（含发型，0 = 未知）。用于限制最大缩放，保证结尾仍保留人物上半身。</summary>
    public double FaceH { get => _faceH; set => Set(ref _faceH, value); }

    private int _motionV;
    /// <summary>
    /// 运镜数据语义版本：0/2 = 旧版（焦点下移、手动运镜选择），
    /// 3 = 按素材自动取景（有人横向轻推、无人横向静止、无人竖幅滑动、有人竖幅人脸居中）。
    /// 旧工程打开或导出时按此重算。
    /// </summary>
    public int MotionV { get => _motionV; set => Set(ref _motionV, value); }

    private double _autoDurBonus;
    /// <summary>
    /// 自动策略已经加过的时长补偿（秒）：无人竖幅照片要上下滑动，多给 1 秒。
    /// 记住这个值，重复应用策略时只补差额，不会越加越多。
    /// </summary>
    public double AutoDurBonus { get => _autoDurBonus; set => Set(ref _autoDurBonus, value); }

    /// <summary>
    /// FaceH 语义版本：0 = 旧版按检测框高度（未含头发修正，偏小导致缩放过猛），
    /// 1 = 头部高度（含 1.45 修正）。打开旧工程时按版本重新检测。
    /// </summary>
    public int FaceHV { get => _faceHv; set => Set(ref _faceHv, value); }
    public bool FaceDetected { get => _faceDetected; set { Set(ref _faceDetected, value); Raise(nameof(FaceText)); } }

    [JsonIgnore] public string ThumbnailPath { get => _thumbnailPath ?? ""; set => Set(ref _thumbnailPath, value); }

    /// <summary>人脸检测诊断信息（方案/状态），仅用于自检日志，不入库。</summary>
    [JsonIgnore] public string FaceDiag { get; set; } = "";

    [JsonIgnore] public string FileName =>
        string.IsNullOrEmpty(SourcePath) ? "(空)"
        // 动图截帧片段显示原始视频名（SourcePath 是哈希名缓存 JPG）
        : !string.IsNullOrEmpty(StillFrom) ? Path.GetFileName(StillFrom)
        : Path.GetFileName(SourcePath);
    [JsonIgnore] public bool IsImage => Kind == MediaKind.Image;
    [JsonIgnore] public bool IsVideo => Kind == MediaKind.Video;
    [JsonIgnore] public double MaxDuration => Kind == MediaKind.Video ? Math.Max(0.5, SourceDuration - SourceIn) : 3600;

    [JsonIgnore] public string DurationText => $"{Duration:0.0}s";
    [JsonIgnore] public string TransitionText => TransactionLabel();
    [JsonIgnore] public string MotionText => Motion.ToDisplay();
    [JsonIgnore] public string FaceText => FaceDetected ? "已识别人脸" : "未识别人脸（按主体自动取景）";

    private string TransactionLabel()
        => Transition == TransitionKind.None ? "无转场" : $"{Transition.ToShort()} {TransitionDuration:0.0}s";

    /// <summary>
    /// 与上一片段之间实际生效的转场时长（受两侧片段时长限制）。
    /// </summary>
    public double EffectiveTransition(Clip? previous)
    {
        if (previous is null || Transition == TransitionKind.None) return 0;
        var limit = Math.Min(previous.Duration, Duration) * 0.8;
        return Math.Min(TransitionDuration, limit);
    }
}
