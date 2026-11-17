using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoriesStudio.Models;
using MemoriesStudio.Services;
using Microsoft.Win32;

namespace MemoriesStudio.Views;

public partial class MainWindow : Window
{
    private static readonly string[] ImageExts =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".jfif" };

    private static readonly string[] VideoExts =
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".3gp", ".3g2", ".mts", ".m2ts",
        ".webm", ".mpg", ".mpeg", ".flv", ".ts", ".insv", ".lrv"
    };

    private static readonly string[] AudioExts =
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".aiff", ".aif", ".opus" };

    private ProjectFile _project = new();
    private readonly ObservableCollection<MediaAsset> _assets = new();

    // 时间轴缩略图缓存：按需加载 + LRU 上限（素材上千时不至于把内存吃光）
    private readonly Services.LruCache<string, BitmapSource> _thumbCache = new(600);
    private readonly HashSet<string> _thumbPending = new();

    private readonly HashSet<Guid> _previewLoading = new();
    // 取景窗预览大图（1280px）：每张约 5MB，必须设上限
    private readonly Services.LruCache<Guid, BitmapSource> _previewCache = new(24);

    private readonly FaceDetector _faceDetector = new();
    private readonly SemaphoreSlim _detectGate = new(2);
    private readonly SemaphoreSlim _probeGate = new(4);   // 同时最多 4 路探测/缩略图，防止大批量导入时抢占解码器
    private CancellationTokenSource? _faceScanCts;   // 批量人脸识别的取消令牌

    // 预览完全静音：配乐与视频原声都在导出时由 ffmpeg 一次性合成
    private readonly VideoFramePlayer _video = new();
    private readonly VideoGrabber _grabber = new();   // 暂停/拖动进度时的单帧快速抓取
    private WriteableBitmap? _videoBitmap;

    private Clip? _selected;
    private string? _projectPath;
    private bool _suppressUi;
    private bool _dirty;          // 有未保存的更改时，关闭程序前询问
    private bool _playing;
    private bool _scrubPreview = true;   // 暂停状态用快速抓帧渲染视频（拖动进度条实时预览）
    private double _time;
    private double _playAnchor;
    private readonly Stopwatch _clock = new();

    private bool _videoNeedsSeek = true;
    private double _videoOpenAt;
    private bool _uiReady;      // InitializeComponent 期间控件会触发 ValueChanged，未就绪时忽略

    // 抓帧队列：仅保留最新请求，旧结果作废
    private (Guid ClipId, double SrcTime)? _scrubWant;
    private (Guid ClipId, double SrcTime)? _scrubDone;
    private bool _scrubBusy;
    private byte[]? _scrubFrameData;
    private (Guid ClipId, double SrcTime)? _scrubFrameKey;

    public MainWindow()
    {
        InitializeComponent();

        AssetsList.ItemsSource = _assets;
        AssetsList.GiveFeedback += OnAssetsDragGiveFeedback;
        _assets.CollectionChanged += (_, _) =>
        {
            TxtAssetCount.Text = $"{_assets.Count} 项";
            if (_assets.Count == 0) TxtAssetCount.Text = "0 项";
        };

        CmbTransition.ItemsSource = TransitionInfo.All
            .Select(t => new Choice<TransitionKind>(t, t.ToDisplay())).ToList();

        Timeline.SelectionChanged += OnTimelineSelectionChanged;
        Timeline.SelectionSetChanged += (_, _) => RefreshInspector();
        Timeline.SeekRequested += OnTimelineSeek;
        Timeline.ThumbnailProvider = GetThumbnailForTimeline;
        Timeline.AssetsDropped += OnTimelineAssetsDropped;
        Timeline.ClipsReordered += OnTimelineClipsReordered;

        AttachProjectEvents(_project);
        CompositionTarget.Rendering += OnRendering;
        Loaded += OnLoaded;
        Closing += OnClosingWindow;

        _uiReady = true;
    }

    // ==================================================================== 启动

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyProjectToUi();
        UpdatePreviewSize();
        RenderFrame();
        UpdateTransportUi();

        if (!FfmpegService.Instance.Available)
        {
            SetStatus("未找到 ffmpeg.exe —— 导出 MP4 时需要它（其它功能不依赖）");
            MessageBox.Show(this,
                "没有找到 ffmpeg.exe。\n\n只有导出 MP4 时需要它，预览、剪辑、探测都不依赖。\n请把 ffmpeg.exe 放到程序所在目录（或 ffmpeg 子目录），或加入系统 PATH 后重启本程序。",
                "缺少 ffmpeg（仅导出需要）", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus("就绪 · 正在准备人脸检测模型…");

        var ok = await FaceDetector.TryEnsureModelAsync();
        if (ok)
        {
            SetStatus("就绪 · 人脸检测已就绪（YuNet）· 拖入素材开始创作");
        }
        else
        {
            FaceDetector.Log("主界面：YuNet 模型缺失，人脸检测退化为显著度/肤色取景");
            SetStatus("就绪 · 未找到人脸模型，将按取景中心取景（详见 %TEMP%\\memories_face.log）");
        }
    }

    // ==================================================================== 素材导入

    private async void OnImportMedia(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图片 / 视频 / 音乐",
            Multiselect = true,
            Filter = "所有支持的素材|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.heic;*.heif;" +
                     "*.mp4;*.mov;*.m4v;*.avi;*.mkv;*.wmv;*.3gp;*.mts;*.m2ts;*.webm;*.mpg;*.mpeg;*.flv;*.ts|图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.heic;" +
                     "*.heif|视频|*.mp4;*.mov;*.m4v;*.avi;*.mkv;*.wmv;*.3gp;*.mts;*.m2ts;*.webm;*.mpg;*.mpeg;*.flv;*.ts|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        await ImportFilesAsync(dlg.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await ImportFilesAsync(paths);
    }

    private static MediaKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // 素材库不收音乐：背景音乐请用「设置 → 添加音乐」
        if (AudioExts.Contains(ext)) return null;
        if (ImageExts.Contains(ext)) return MediaKind.Image;
        if (VideoExts.Contains(ext)) return MediaKind.Video;
        return null;
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var f in files) yield return f;
            }
            else if (File.Exists(p))
            {
                yield return p;
            }
        }
    }

    /// <param name="healthCheck">手动导入为 true：后台 6 路并行给新图片做完整解码体检（不阻塞界面）；
    /// 打开工程重建素材库传 false——体检在打开路径上太慢（1280+ 张图要数分钟），缓存结论随后续操作按需命中。</param>
    private async Task ImportFilesAsync(IEnumerable<string> paths, bool healthCheck = true)
    {
        var all = ExpandPaths(paths).ToList();
        var audioCount = all.Count(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
        var files = all.Where(f => KindOf(f) is not null).ToList();
        if (files.Count == 0)
        {
            SetStatus(audioCount > 0
                ? "素材库不收音乐：请点顶部「设置 → 添加音乐」加入背景音乐。"
                : "没有找到可导入的图片 / 视频。");
            return;
        }

        var added = 0;
        var skipped = 0;
        foreach (var f in files)
        {
            var kind = KindOf(f);
            if (kind is null) continue;
            if (_assets.Any(a => string.Equals(a.Path, f, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            _assets.Add(new MediaAsset { Path = f, Kind = kind.Value });
            added++;
        }

        // 后台逐个补齐探测信息与缩略图（计数已由 CollectionChanged 实时刷新）
        foreach (var a in _assets.Where(a => a.Width == 0 && a.Kind != MediaKind.Audio).ToList())
            _ = LoadAssetInfoAsync(a);

        // 手动导入：新图片后台批量体检（6 路并行完整解码），损坏的即时标红；缓存命中的秒过
        if (healthCheck)
            _ = CheckNewImagesHealthAsync();

        SetStatus(audioCount > 0
            ? $"已导入 {added} 个素材（{audioCount} 个音乐文件未收：请用「设置 → 添加音乐」）· 双击加入时间轴，或直接拖到时间轴上插入"
            : skipped > 0
                ? $"找到 {files.Count} 个文件：新增 {added} 个、跳过已有 {skipped} 个 · 双击素材加入，或拖到时间轴指定位置"
                : $"已导入 {added} 个素材 · 双击加入时间轴，或直接拖到时间轴上插入");
    }

    /// <summary>后台批量体检素材库图片：6 路并行完整解码，损坏的标红并提示。缓存命中的零开销。</summary>
    private async Task CheckNewImagesHealthAsync()
    {
        var images = _assets.Where(a => a.Kind == MediaKind.Image && !a.Corrupt).ToList();
        if (images.Count == 0) return;
        try
        {
            var bad = await ImageHealth.CheckManyAsync(
                images.Select(a => a.Path), (done, total) =>
                    _ = Dispatcher.InvokeAsync(() =>
                        SetStatus($"正在体检图片素材 {done}/{total}…")));
            if (bad.Count == 0) return;
            foreach (var a in images.Where(a => bad.Contains(a.Path, StringComparer.OrdinalIgnoreCase)))
                a.Corrupt = true;
            SetStatus($"⚠ {bad.Count} 个图片素材已损坏（已在素材库中标红）：加入时间轴会被拒绝，导出时用灰帧替代");
        }
        catch { /* 体检失败不影响导入 */ }
    }

    private async Task LoadAssetInfoAsync(MediaAsset asset)
    {
        if (string.IsNullOrEmpty(asset.Path) || !File.Exists(asset.Path)) return;
        await _probeGate.WaitAsync();
        try
        {
            var probe = await MediaProbe.ProbeAsync(asset.Path);
            var time = asset.Kind == MediaKind.Video ? Math.Min(1.0, Math.Max(0.1, probe?.Duration * 0.1 ?? 0)) : 0;
            var thumb = await MediaProbe.CreateThumbnailAsync(asset.Path, time, 220, asset.Kind);

            // 图片完整解码体检不在这里做（1280+ 张图首开会拖慢打开工程数分钟）：
            // 缓存里已有结论的直接标红（零开销），完整解码只在手动导入（后台并行）、
            // 加入时间轴（批量并行）和导出前体检时进行，结论共用 ImageHealth 缓存
            var corrupt = asset.Kind == MediaKind.Image
                && ImageHealth.CachedVerdict(asset.Path) == true;

            // 所有属性更新必须回到 UI 线程，避免后台线程触发绑定更新导致异常
            Dispatcher.Invoke(() =>
            {
                if (probe is not null)
                {
                    asset.Width = probe.Width;
                    asset.Height = probe.Height;
                    asset.Duration = probe.Duration;
                    asset.Fps = probe.Fps;
                    asset.HasAudio = probe.HasAudio;
                }
                if (thumb is not null)
                {
                    asset.ThumbnailPath = thumb;
                }
                if (corrupt)
                {
                    asset.Corrupt = true;
                    SetStatus($"⚠ 素材「{asset.FileName}」文件损坏（解码失败）：建议从备份重新拷贝；导出时将自动用灰帧替代");
                }

                // 探测信息晚到：回填同路径的片段（分辨率 / 原声 / 素材总时长），
                // 若片段时长还是旧默认值，一并恢复为素材实际时长
                var touched = false;
                foreach (var c in _project.Clips.Where(c => c.SourcePath == asset.Path && c.Kind != MediaKind.Image))
                {
                    if (probe is not null)
                    {
                        ApplyProbeToClip(c, probe);
                        touched = true;
                    }
                    if (c.Kind == MediaKind.Video && IsLegacyDefaultDuration(c) && c.SourceDuration > 0.5)
                    {
                        c.Duration = Math.Max(0.5, c.SourceDuration - c.SourceIn);
                        touched = true;
                    }
                }
                if (thumb is not null)
                    foreach (var c in _project.Clips.Where(c => c.SourcePath == asset.Path))
                        c.ThumbnailPath = thumb;

                if (touched)
                {
                    RefreshAfterTimelineEdit();
                    if (ReferenceEquals(_selected, _project.Clips.FirstOrDefault(c => c.SourcePath == asset.Path)) ||
                        (_selected is not null && _selected.SourcePath == asset.Path))
                        RefreshInspector();
                }
                Timeline.Refresh();
            });
        }
        catch { /* 单个素材失败不影响整体 */ }
        finally
        {
            _probeGate.Release();
        }
    }

    private async void OnAssetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击 = 加入当前选中（含双击项），按素材库顺序
        var items = AssetsList.SelectedItems.Cast<MediaAsset>()
            .OrderBy(a => _assets.IndexOf(a)).ToList();
        if (items.Count == 0) return;
        await AddAssetsToTimelineAsync(items);
    }

    private async void OnAddSelectedToTimeline(object sender, RoutedEventArgs e)
    {
        var items = AssetsList.SelectedItems.Cast<MediaAsset>()
            .OrderBy(a => _assets.IndexOf(a)).ToList();
        if (items.Count == 0)
        {
            // 未选中 = 加入素材库里的全部图片 / 视频（损坏的自动跳过）
            items = _assets.Where(a => a.Kind != MediaKind.Audio && !a.Corrupt)
                .OrderBy(a => _assets.IndexOf(a)).ToList();
            if (items.Count == 0)
            {
                SetStatus("素材库是空的，先导入素材。");
                return;
            }
        }
        await AddAssetsToTimelineAsync(items);
    }

    /// <summary>确保素材已探测（分辨率 / 时长就绪），避免把 0 值写进片段。</summary>
    private async Task EnsureAssetsProbedAsync(IEnumerable<MediaAsset> assets)
    {
        foreach (var a in assets)
        {
            var needsProbe = a.Kind == MediaKind.Audio
                ? a.Duration <= 0
                : a.Width <= 0 || (a.Kind == MediaKind.Video && a.Duration <= 0);
            if (!needsProbe || !File.Exists(a.Path)) continue;
            await LoadAssetInfoAsync(a);
        }
    }

    private async Task AddAssetsToTimelineAsync(IEnumerable<MediaAsset> assets, double? atTime = null)
    {
        var list = assets.ToList();

        // 视频片段必须以真实时长入轨：先把未完成的探测补齐（有并发闸门，很快）。
        // 探测会顺带做图片体检（IsBadAsync），损坏素材在此被拦下，不允许入轨
        SetStatus("正在读取素材信息…");
        await EnsureAssetsProbedAsync(list);

        var corrupt = list.Where(a => a.Corrupt).ToList();

        // 图片批量体检：缓存命中的零开销，未缓存的 6 路并行解码（打开工程时没检过的在此兜底）
        var imgPaths = list.Where(a => a.Kind == MediaKind.Image && !a.Corrupt)
            .Select(a => a.Path).Distinct().ToList();
        if (imgPaths.Count > 0)
        {
            var bad = await ImageHealth.CheckManyAsync(imgPaths, null);
            foreach (var a in list.Where(a => a.Kind == MediaKind.Image && bad.Contains(a.Path, StringComparer.OrdinalIgnoreCase)))
            {
                a.Corrupt = true;
                corrupt.Add(a);
            }
        }

        if (corrupt.Count > 0)
        {
            list = list.Except(corrupt).ToList();
            if (list.Count == 0)
            {
                SetStatus($"⚠ {corrupt.Count} 个素材文件已损坏，无法加入时间轴（已在素材库中标红）");
                return;
            }
        }
        var corruptCount = corrupt.Count;

        var count = 0;
        Clip? lastAdded = null;
        var pendingFace = new List<Clip>();   // 本次新加的图片片段，统一跑人脸识别（带进度）

        // 插入位置：拖放落点 > 播放头位置 > 末尾
        var insertAt = atTime.HasValue
            ? InsertIndexForTime(atTime.Value)
            : InsertIndexForPlayhead();

        foreach (var a in list)
        {
            // 视频 = 实际时长（用户可通过节选区间裁剪）；图片 = 4 秒；音乐不进时间轴
            if (a.Kind == MediaKind.Audio) continue;

            // 动图（小于 2 秒的视频）：在时长 1/2 处截一帧，按图片入轨。
            // 这类片段本身就是"会动的照片"，按视频入轨只有一两秒、转场都放不下，
            // 截帧当照片反而更自然（可用照片时长/运镜/转场）。
            var isLivePhoto = a.Kind == MediaKind.Video && a.Duration > 0.2 && a.Duration < 2.0;
            string? still = null;
            if (isLivePhoto)
                still = await Task.Run(() => MediaSource.ExtractStill(a.Path, a.Duration / 2));

            var duration = a.Kind == MediaKind.Image || still is not null
                ? 4.0
                : Math.Max(0.5, a.Duration > 0.2 ? a.Duration : 5.0);

            var clip = new Clip
            {
                SourcePath = still ?? a.Path,
                Kind = still is not null ? MediaKind.Image : a.Kind,
                StillFrom = still is not null ? a.Path : null,
                SourceWidth = a.Width,
                SourceHeight = a.Height,
                SourceDuration = a.Duration,
                HasAudio = still is null && a.HasAudio,
                Duration = duration,
                ThumbnailPath = a.ThumbnailPath ?? "",
                Transition = TransitionKind.CrossFade,
                TransitionDuration = 1.0,
                Motion = MotionStyle.Auto
            };
            if (still is not null) clip.ThumbnailPath = still;

            _project.Clips.Insert(Math.Min(insertAt, _project.Clips.Count), clip);
            insertAt++;
            count++;
            lastAdded = clip;

            if (clip.Kind == MediaKind.Image) pendingFace.Add(clip);
        }

        if (count > 0)
        {
            if (lastAdded is not null)
            {
                _selected = lastAdded;
                Timeline.SetClipSelection(new[] { lastAdded }, lastAdded);
            }
            MarkDirty();
            RefreshInspector();
            var skipNote = corruptCount > 0 ? $" · ⚠ {corruptCount} 个损坏素材已跳过" : "";
            SetStatus($"已加入 {count} 个素材（第 {insertAt - count + 1} 个位置起）· 总时长 {_project.TotalDuration:0.0} 秒{skipNote}");
        }

        // 首次导入即完成人脸识别：显示进度窗并阻塞操作，全部识别完（或取消）后返回，
        // 之后运镜/导出拿到的都是完整检测数据
        if (pendingFace.Count > 0)
            await RunFaceScanAsync(pendingFace);
    }

    /// <summary>按播放头位置计算插入索引。</summary>
    private int InsertIndexForPlayhead()
    {
        var (curIdx, _) = _project.Locate(_time);
        return curIdx < 0 ? _project.Clips.Count
             : _time <= 0.001 ? 0
             : curIdx + 1;
    }

    /// <summary>按时间轴落点计算插入索引：落点在某片段前半段 → 插到它前面，后半段 → 插到它后面。</summary>
    private int InsertIndexForTime(double t)
    {
        var clips = _project.Clips;
        if (clips.Count == 0) return 0;
        var starts = _project.ComputeStarts();
        for (var i = 0; i < clips.Count; i++)
        {
            var end = starts[i] + clips[i].Duration;
            if (t < end - 1e-6)
                return (t - starts[i]) < clips[i].Duration / 2 ? i : i + 1;
        }
        return clips.Count;
    }

    // ==================================================================== 素材库拖拽到时间轴

    private Point _assetsDragOrigin;
    private bool _assetsDragArmed;

    private void OnAssetsListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _assetsDragArmed = true;
        _assetsDragOrigin = e.GetPosition(AssetsList);
    }

    private void OnAssetsListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _assetsDragArmed = false; return; }
        if (!_assetsDragArmed) return;

        var pos = e.GetPosition(AssetsList);
        if (Math.Abs(pos.X - _assetsDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _assetsDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // 损坏素材不允许拖入时间轴（选中其余项时自动排除它们）
        var items = AssetsList.SelectedItems.Cast<MediaAsset>()
            .Where(a => !a.Corrupt)
            .OrderBy(a => _assets.IndexOf(a)).ToList();
        if (items.Count == 0)
        {
            SetStatus("选中的素材已损坏，无法拖入时间轴。");
            _assetsDragArmed = false;
            return;
        }
        _assetsDragArmed = false;
        var data = new DataObject("MemoriesStudioAssets", items);
        // 拖动期间光标换成"图片/视频标志 + 数量角标"（与时间轴内部拖动一致）
        _assetsDragCursor = Timeline.GetDragCursor(items.Count, items.All(a => a.Kind == MediaKind.Video));
        try { DragDrop.DoDragDrop(AssetsList, data, DragDropEffects.Copy); }
        finally { _assetsDragCursor = null; }
    }

    private Cursor? _assetsDragCursor;

    private void OnAssetsDragGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_assetsDragCursor is null) return;
        e.UseDefaultCursors = false;
        Mouse.SetCursor(_assetsDragCursor);
        e.Handled = true;
    }

    private async void OnTimelineAssetsDropped(object? sender,
        (System.Collections.Generic.IReadOnlyList<MediaAsset> Assets, double Time) payload)
    {
        var items = payload.Assets
            .Where(a => a.Kind != MediaKind.Audio)
            .OrderBy(a => _assets.IndexOf(a)).ToList();
        if (items.Count == 0)
        {
            SetStatus("背景音乐请在顶部「设置」里添加。");
            return;
        }
        await AddAssetsToTimelineAsync(items, Math.Max(0, payload.Time));
    }

    // ==================================================================== 人脸 / 运镜

    /// <summary>单片段后台检测（不弹窗）。打开旧工程补检时使用。</summary>
    private async Task DetectFaceAsync(Clip clip)
    {
        if (clip.Kind != MediaKind.Image) return;
        await _detectGate.WaitAsync();
        try
        {
            var result = await Task.Run(() => _faceDetector.Detect(clip.SourcePath));
            await Dispatcher.InvokeAsync(() => ApplyFaceResult(clip, result));
        }
        catch { }
        finally
        {
            _detectGate.Release();
        }
    }

    /// <summary>把检测结果写入片段并联动 UI。必须在 UI 线程调用。</summary>
    private void ApplyFaceResult(Clip clip, FaceDetector.FocusResult result)
    {
        var (nx, ny) = CoverMath.ToCanvas(result.X, result.Y,
            clip.SourceWidth, clip.SourceHeight, _project.Width, _project.Height);
        var faceH = CoverMath.FaceHeightToCanvas(result.FaceHeight,
            clip.SourceWidth, clip.SourceHeight, _project.Width, _project.Height);

        clip.FaceX = nx;
        clip.FaceY = ny;
        clip.FaceSY = Math.Clamp(result.Y, 0.01, 0.99);   // 原图坐标：竖幅纵向平移靠它取景
        clip.FaceH = faceH;
        clip.FaceHV = 2;   // 标记为 YuNet 检测语义，避免旧工程重复重检
        clip.FaceDetected = result.FaceFound;
        clip.FaceDiag = $"{result.Method}/{result.Status}";
        MotionPresets.Apply(clip, _project.Clips.IndexOf(clip), _project.Width, _project.Height);

        if (ReferenceEquals(clip, _selected)) RefreshInspector();
        Timeline.Refresh();
        RenderFrame();
    }

    /// <summary>
    /// 批量人脸识别：显示进度窗、禁用主窗口（识别完才能继续操作），
    /// 2 路并行跑检测；用户可取消，取消后剩余片段保持默认取景。
    /// </summary>
    private async Task RunFaceScanAsync(List<Clip> pending)
    {
        if (pending.Count == 0) return;
        if (pending.Count == 1)
        {
            SetStatus($"正在人脸识别：{pending[0].FileName}");
            await DetectFaceAsync(pending[0]);
            SetStatus($"人脸识别完成 · 总时长 {_project.TotalDuration:0.0} 秒");
            return;
        }

        var total = pending.Count;
        var dlg = new FaceScanWindow { Owner = this };
        _faceScanCts = new CancellationTokenSource();
        dlg.Cts = _faceScanCts;
        dlg.Report(0, total, "准备中…");

        var done = 0;
        var next = -1;
        var cancelled = false;
        IsEnabled = false;
        dlg.Show();
        try
        {
            // 2 路并行（与 _detectGate 的并发度一致，避免多张图抢占解码器内存）
            var workers = Enumerable.Range(0, Math.Min(2, total)).Select(async _ =>
            {
                while (true)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= total) return;
                    if (_faceScanCts.IsCancellationRequested) { cancelled = true; return; }
                    var clip = pending[i];
                    try
                    {
                        var result = await Task.Run(() => _faceDetector.Detect(clip.SourcePath));
                        if (_faceScanCts.IsCancellationRequested) { cancelled = true; return; }
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ApplyFaceResult(clip, result);
                            done++;
                            dlg.Report(done, total, clip.FileName);
                        });
                    }
                    catch
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            done++;
                            dlg.Report(done, total, clip.FileName);
                        });
                    }
                }
            }).ToList();
            await Task.WhenAll(workers);
        }
        finally
        {
            IsEnabled = true;
            try { dlg.Close(); } catch { }
            _faceScanCts.Dispose();
            _faceScanCts = null;
        }

        SetStatus(cancelled
            ? $"人脸识别已取消（完成 {done}/{total}），未识别的照片按默认取景 · 可用「按素材自动取景」重新识别"
            : $"人脸识别完成（{total} 张）· 总时长 {_project.TotalDuration:0.0} 秒");
    }

    private async void OnAutoMotionAll(object sender, RoutedEventArgs e)
    {
        var images = _project.Clips.Where(c => c.Kind == MediaKind.Image).ToList();
        if (images.Count == 0)
        {
            SetStatus("时间轴里还没有照片。");
            return;
        }

        SetStatus($"正在为 {images.Count} 张照片重新检测人脸并生成运镜…");
        var done = 0;
        foreach (var clip in images)
        {
            await DetectFaceAsync(clip);
            done++;
            SetStatus($"正在分析 {done}/{images.Count}：{clip.FileName}");
        }

        foreach (var clip in _project.Clips) MotionPresets.Apply(clip, _project.Clips.IndexOf(clip), _project.Width, _project.Height);
        Timeline.Refresh();
        RefreshInspector();
        RenderFrame();
        SetStatus($"已按素材自动取景（有人横向轻推 / 无人横向静止 / 竖幅上下滑动 / 人像居中），共 {images.Count} 张");
    }

    // ==================================================================== 时间轴 / 属性

    private void OnTimelineSelectionChanged(object? sender, Clip? clip)
    {
        _selected = clip;
        RefreshInspector();
        RenderFrame();
        if (clip is not null) RevealAssetInList(clip);
    }

    /// <summary>
    /// 时间轴上选中片段后，把素材库中对应的那一项滚到列表中间并高亮，
    /// 让用户在几百个素材里立刻对上"这个片段来自哪张图 / 哪段视频"。
    /// </summary>
    private void RevealAssetInList(Clip clip)
    {
        // 动图片段的 SourcePath 是缓存截帧，素材库里对应的是原视频（StillFrom）
        var path = string.IsNullOrEmpty(clip.StillFrom) ? clip.SourcePath : clip.StillFrom;
        var asset = _assets.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                    ?? _assets.FirstOrDefault(a => string.Equals(a.Path, clip.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return;

        // 只做定位高亮，不抢键盘焦点（焦点仍在时间轴上，方向键继续可用）
        if (!AssetsList.SelectedItems.Contains(asset)) AssetsList.SelectedItem = asset;
        CenterListBoxItem(AssetsList, asset);
    }

    /// <summary>把列表项滚到可视区正中。</summary>
    private static void CenterListBoxItem(ListBox list, object item)
    {
        list.ScrollIntoView(item);
        list.UpdateLayout();

        var sv = FindDescendant<ScrollViewer>(list);
        if (sv is null) return;
        var max = Math.Max(0, sv.ScrollableHeight);

        // 虚拟化列表（CanContentScroll=true）的 Offset / ViewportHeight 单位是"项"，
        // 不能拿像素坐标直接相加——按项索引居中。
        if (sv.CanContentScroll)
        {
            var index = list.Items.IndexOf(item);
            if (index < 0) return;
            sv.ScrollToVerticalOffset(Math.Clamp(index - sv.ViewportHeight / 2 + 0.5, 0, max));
            return;
        }

        if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) return;
        if (sv.ViewportHeight <= 0) return;
        var center = container.TranslatePoint(new Point(0, container.ActualHeight / 2), sv).Y;
        sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + center - sv.ViewportHeight / 2, 0, max));
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>光标键左右：横向滚动时间轴视口。</summary>
    private void ScrollTimelineBy(double pixels)
    {
        TimelineScroll.UpdateLayout();   // 连续按键落在同一帧时先刷新 Offset，避免丢步
        var max = Math.Max(0, TimelineScroll.ScrollableWidth);
        TimelineScroll.ScrollToHorizontalOffset(
            Math.Clamp(TimelineScroll.HorizontalOffset + pixels, 0, max));
    }

    /// <summary>光标键上下：逐行滚动素材库（只滚视口，不改选择）。</summary>
    private void ScrollAssetsBy(int rows)
    {
        var sv = FindDescendant<ScrollViewer>(AssetsList);
        if (sv is null) return;

        sv.UpdateLayout();   // 同上：先让上一次滚动的 Offset 落地

        // 虚拟化列表的 Offset 以项为单位，滚 1 项即一行；非虚拟化时按像素（一行约 62px）
        var step = sv.CanContentScroll ? 1.0 : 62.0;
        var max = Math.Max(0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + rows * step, 0, max));
    }

    private void OnTimelineSeek(object? sender, double time) => PreviewSeek(time);

    /// <summary>时间轴上拖动片段调整顺序后：刷新界面并标记工程已修改。</summary>
    private void OnTimelineClipsReordered(object? sender, EventArgs e)
    {
        MarkDirty();
        Timeline.Refresh();
        RefreshAfterTimelineEdit();
        _videoNeedsSeek = true;
        RenderFrame();

        if (_selected is { } clip)
        {
            var i = _project.Clips.IndexOf(clip) + 1;
            SetStatus($"已移动「{clip.FileName}」到第 {i}/{_project.Clips.Count} 位");
        }
        else
        {
            SetStatus("已调整片段顺序");
        }
    }

    private void RefreshAfterTimelineEdit()
    {
        Timeline.Project = _project;
        Timeline.Refresh();
        _suppressUi = true;
        SeekSlider.Maximum = Math.Max(0.1, _project.TotalDuration);
        _suppressUi = false;
        TxtTimelineInfo.Text = $"{_project.Clips.Count} 个片段 · {_project.TotalDuration:0.0}s";
        PreviewHint.Visibility = _project.Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTransportUi();
    }

    private void RefreshInspector()
    {
        _suppressUi = true;
        try
        {
            var clips = Timeline.SelectedClips.ToList();

            var showClip = _selected is not null && clips.Count <= 1;
            ClipPanel.Visibility = showClip ? Visibility.Visible : Visibility.Collapsed;

            MultiPanel.Visibility = clips.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (clips.Count > 1)
                TxtMultiInfo.Text = $"已选中 {clips.Count} 个片段 · 合计 {clips.Sum(c => c.Duration):0.0}s";

            TxtEmptyInspector.Visibility = _selected is null && clips.Count <= 1
                ? Visibility.Visible : Visibility.Collapsed;

            if (showClip)
            {
                var c = _selected!;
                TxtClipName.Text = c.FileName;

                SldDuration.Maximum = c.Kind == MediaKind.Video
                    ? Math.Max(1.0, c.MaxDuration)
                    : 20.0;
                SldDuration.Value = Math.Min(c.Duration, SldDuration.Maximum);
                TxtClipDuration.Text = $"{c.Duration:0.0}s";

                // 视频：不提供拖动条，只显示原视频长度 / 剪辑后长度（调整走剪辑对话框）
                var isVideo = c.Kind == MediaKind.Video;
                DurPanel.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
                TrimPanel.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
                if (isVideo)
                {
                    if (c.SourceDuration > 0.3)
                    {
                        TxtTrimSrcLen.Text = $"{c.SourceDuration:0.0}s";
                        TxtTrimLen.Text = $"{c.Duration:0.0}s";
                    }
                    else
                    {
                        TxtTrimSrcLen.Text = "读取中…";
                        TxtTrimLen.Text = $"{c.Duration:0.0}s";
                    }
                }

                CmbTransition.SelectedItem = CmbTransition.Items.Cast<Choice<TransitionKind>>()
                    .FirstOrDefault(x => x.Value == c.Transition);
                SldTransition.Value = c.TransitionDuration;
                TxtTransitionDur.Text = $"{c.TransitionDuration:0.0}s";
                TxtFaceStatus.Text = c.FaceText;
            }
        }
        finally
        {
            _suppressUi = false;
        }

        UpdateFaceBox(_selected);
    }

    private void OnDurationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || _selected is null) return;
        _selected.Duration = e.NewValue;

        // 视频加长时保证 入点+时长 不超过素材实际长度
        if (_selected.Kind == MediaKind.Video && _selected.SourceDuration > 0.3)
            _selected.SourceIn = Math.Clamp(_selected.SourceIn, 0,
                Math.Max(0, _selected.SourceDuration - _selected.Duration));

        TxtClipDuration.Text = $"{e.NewValue:0.0}s";
        if (TrimPanel.Visibility == Visibility.Visible)
        {
            TxtTrimSrcLen.Text = _selected.SourceDuration > 0.3
                ? $"{_selected.SourceDuration:0.0}s" : "读取中…";
            TxtTrimLen.Text = $"{_selected.Duration:0.0}s";
        }
        MarkDirty();
        Timeline.Refresh();
        RefreshAfterTimelineEdit();
        RenderFrame();
    }

    private void OnTransitionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || _selected is null) return;
        if (CmbTransition.SelectedItem is not Choice<TransitionKind> choice) return;
        MarkDirty();
        _selected.Transition = choice.Value;
        Timeline.Refresh();
        RenderFrame();
    }

    /// <summary>
    /// 打开剪辑对话框：LibVLC 播放本段视频（有声），用区间条精确选择保留区间，
    /// 确定后把起点/时长写回片段。
    /// </summary>
    private void OnOpenTrimEditor(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Kind: MediaKind.Video } clip) return;
        if (_selected.SourceDuration <= 0.3)
        {
            SetStatus("正在读取素材信息，稍候再试。");
            return;
        }
        if (string.IsNullOrEmpty(clip.SourcePath) || !File.Exists(clip.SourcePath))
        {
            SetStatus("视频文件不存在。");
            return;
        }

        if (_playing) PausePlayback();

        var dlg = new TrimWindow(clip) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var src = clip.SourceDuration;
        var inPt = Math.Clamp(dlg.InPoint, 0, Math.Max(0, src - 0.3));
        var outPt = Math.Clamp(dlg.OutPoint, inPt + 0.3, src);
        if (Math.Abs(inPt - clip.SourceIn) < 0.01 && Math.Abs((outPt - inPt) - clip.Duration) < 0.01) return;

        clip.SourceIn = inPt;
        clip.Duration = outPt - inPt;

        _suppressUi = true;
        try
        {
            TxtTrimSrcLen.Text = $"{src:0.0}s";
            TxtTrimLen.Text = $"{clip.Duration:0.0}s";
            TxtClipDuration.Text = $"{clip.Duration:0.0}s";
            SldDuration.Maximum = Math.Max(1.0, clip.MaxDuration);
            SldDuration.Value = clip.Duration;
        }
        finally
        {
            _suppressUi = false;
        }

        MarkDirty();
        Timeline.Refresh();
        RefreshAfterTimelineEdit();
        _videoNeedsSeek = true;
        RenderFrame();
        SetStatus($"已调整节选区间：{clip.SourceIn:0.0}s ~ {clip.SourceIn + clip.Duration:0.0}s（共 {clip.Duration:0.0}s）");
    }

    private void OnTransitionDurationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi || _selected is null) return;
        MarkDirty();
        _selected.TransitionDuration = e.NewValue;
        TxtTransitionDur.Text = $"{e.NewValue:0.0}s";
        Timeline.Refresh();
        RenderFrame();
    }

    private void OnClipUp(object sender, RoutedEventArgs e) => MoveClip(-1);
    private void OnClipDown(object sender, RoutedEventArgs e) => MoveClip(1);

    private void MoveClip(int delta)
    {
        if (_selected is null) return;
        var idx = _project.Clips.IndexOf(_selected);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= _project.Clips.Count) return;
        MarkDirty();
        _project.Clips.Move(idx, target);
        Timeline.SetClipSelection(new[] { _selected }, _selected);
        RefreshInspector();
        SetStatus("片段顺序已调整");
    }

    private void OnRemoveClip(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        RemoveClips(new[] { _selected });
    }

    /// <summary>删除多选的全部片段（Ctrl 多选后按 Del 或右侧按钮）。</summary>
    private void OnRemoveSelectedClips(object sender, RoutedEventArgs e)
        => RemoveClips(Timeline.SelectedClips.ToList());

    private void RemoveClips(IReadOnlyList<Clip> clips)
    {
        if (clips.Count == 0) return;

        var label = clips.Count == 1 ? clips[0].FileName : $"{clips.Count} 个片段";
        MarkDirty();
        foreach (var c in clips.ToList()) _project.Clips.Remove(c);

        _selected = null;
        Timeline.SetClipSelection(Array.Empty<Clip>());
        RefreshInspector();
        RenderFrame();
        SetStatus($"已删除 {label}");
    }

    // ==================================================================== 全局设置

    private void OnGlobalSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_project) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var geometryChanged = dlg.WidthOut != _project.Width || dlg.HeightOut != _project.Height;
        MarkDirty();
        _project.Width = dlg.WidthOut;
        _project.Height = dlg.HeightOut;
        _project.Fps = dlg.FpsOut;
        TxtResBadge.Text = $"{_project.Width}×{_project.Height} · {_project.Fps}fps";
        SetStatus(dlg.MusicChanged
            ? $"背景音乐已更新：{_project.MusicPaths.Count} 首（导出时循环播放、只配在照片段、自动淡入淡出）"
            : $"输出设置：{_project.Width}×{_project.Height} · {_project.Fps}fps");

        if (geometryChanged)
        {
            // 画布比例变了：竖幅平移 / 取景参数要按新画布重算，否则预览与导出都会错位
            _previewCache.Clear();
            _previewLoading.Clear();
            UpdatePreviewSize();
            for (var i = 0; i < _project.Clips.Count; i++)
            {
                var c = _project.Clips[i];
                if (c.Kind == MediaKind.Image) MotionPresets.Apply(c, i, _project.Width, _project.Height);
            }
        }

        Timeline.Refresh();
        RenderFrame();
        if (!dlg.MusicChanged)
            SetStatus($"输出设置：{_project.Width}×{_project.Height} · {_project.Fps}fps");
    }

    // ==================================================================== 预览渲染

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewSize();

    private void UpdatePreviewSize()
    {
        var aw = PreviewHost.ActualWidth - 28;
        var ah = PreviewHost.ActualHeight - 28;
        if (aw < 20 || ah < 20) return;

        var ar = _project.AspectRatio;
        double w = aw, h = aw / ar;
        if (h > ah)
        {
            h = ah;
            w = ah * ar;
        }
        PreviewRoot.Width = Math.Floor(w);
        PreviewRoot.Height = Math.Floor(h);
    }

    private void RenderFrame()
    {
        var clips = _project.Clips;
        if (clips.Count == 0)
        {
            ImgA.Source = null;
            ImgB.Source = null;
            PreviewHint.Visibility = Visibility.Visible;
            PreviewBadge.Visibility = Visibility.Collapsed;
            FaceBox.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewHint.Visibility = Visibility.Collapsed;
        var (idx, local) = _project.Locate(_time);
        if (idx < 0) return;

        var cur = clips[idx];
        var td = idx > 0 ? cur.EffectiveTransition(clips[idx - 1]) : 0;
        var inTransition = td > 0.001 && local < td - 1e-6;

        ResetLayerEffects();

        if (inTransition)
        {
            var prev = clips[idx - 1];
            FillLayer(ImgA, prev, prev.Duration - td + local, false);
            HostB.Visibility = Visibility.Visible;
            FillLayer(ImgB, cur, local, true);
            ApplyTransition(cur.Transition, Math.Clamp(local / td, 0, 1));
        }
        else
        {
            FillLayer(ImgA, cur, local, true);
            HostB.Visibility = Visibility.Collapsed;
        }

        PreviewBadge.Visibility = Visibility.Visible;
        TxtPreviewBadge.Text = inTransition
            ? $"{cur.Transition.ToShort()} → {cur.FileName}"
            : $"{idx + 1}/{clips.Count} · {cur.FileName}" + (cur.Kind == MediaKind.Video ? " · 视频" : "");

        UpdateFaceBox(cur);
    }

    private void ResetLayerEffects()
    {
        HostA.Opacity = 1;
        HostB.Opacity = 1;
        HostA.Clip = null;
        HostB.Clip = null;
        HostB.RenderTransform = null;
        // 还原宿主默认尺寸/裁剪（cover 布局会临时改宽高）
        HostA.Width = double.NaN;
        HostB.Width = double.NaN;
        HostA.Height = double.NaN;
        HostB.Height = double.NaN;
        HostA.ClipToBounds = true;
        HostB.ClipToBounds = true;
    }

    private void FillLayer(Image img, Clip clip, double local, bool primary)
    {
        if (clip.Kind == MediaKind.Image)
        {
            var bmp = GetPreviewImage(clip);
            img.Source = bmp;
            ApplyKenBurns(img, clip, local);
            return;
        }

        if (primary) EnsureVideo(clip, local);

        byte[]? data;
        int fw, fh;
        if (_scrubPreview)
        {
            // 快速抓帧预览：只显示与当前片段/时间匹配的最新抓帧结果
            if (_scrubFrameData is not null && _scrubFrameKey == (clip.Id, clip.SourceIn + local))
            {
                data = _scrubFrameData;
                fw = VideoGrabber.GrabWidth;
                fh = VideoGrabber.GrabHeight;
            }
            else return;   // 抓帧尚未返回，保留当前画面
        }
        else
        {
            data = _video.GetFrame(clip.SourceIn + local);
            fw = _video.FrameWidth;
            fh = _video.FrameHeight;
        }

        if (data is not null && fw > 0)
        {
            EnsureVideoBitmap(fw, fh);
            _videoBitmap!.WritePixels(new Int32Rect(0, 0, fw, fh), data, fw * 4, 0);
            img.Source = _videoBitmap;
        }
        else if (_videoBitmap is not null)
        {
            img.Source = _videoBitmap;
        }
        ResetImageLayout(img);
        img.RenderTransform = Transform.Identity;
    }

    /// <summary>视频帧按画布铺满（竖幅视频居中裁切，与导出一致），清掉图片路径留下的显式布局。</summary>
    private static void ResetImageLayout(Image img)
    {
        img.Width = double.NaN;
        img.Height = double.NaN;
        img.HorizontalAlignment = HorizontalAlignment.Stretch;
        img.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void ApplyKenBurns(Image img, Clip clip, double local)
    {
        var w = PreviewRoot.ActualWidth;
        var h = PreviewRoot.ActualHeight;
        if (w < 4 || h < 4) return;

        var p = clip.Duration <= 0.001 ? 0 : Math.Clamp(local / clip.Duration, 0, 1);
        var st = MotionMath.At(clip, p);
        var z = Math.Max(1.0, st.Zoom);

        var iw = clip.SourceWidth > 0 ? clip.SourceWidth : (img.Source as BitmapSource)?.PixelWidth ?? 1;
        var ih = clip.SourceHeight > 0 ? clip.SourceHeight : (img.Source as BitmapSource)?.PixelHeight ?? 1;
        var host = ReferenceEquals(img, ImgA) ? HostA : HostB;

        if (clip.PanV == 1)
        {
            // 竖幅纵向平移：整张照片按 cover 尺寸铺开（上下超出画布的部分也可到达），
            // 画布视窗沿原图滑动。与导出端的 scale+crop(逐帧 y) 几何一致。
            var (dw, dh, tx, ty) = CoverMath.Window(iw, ih, w, h, 1.0, st.FocusX, st.FocusY);
            // 关键：把宿主 Grid 撑到 cover 高度，让 Image 的测量/排列/绘制三者一致。
            // 若只拉高 Image 而宿主仍是画布高，绘制会被钳制在测量 slot 高度 → 底部黑带。
            // 超出画布的部分由 PreviewRoot.ClipToBounds 裁剪。
            host.Height = dh;
            host.ClipToBounds = false;
            img.Stretch = Stretch.Fill;                 // 元素比例=照片比例，不会变形
            ResetImageLayout(img);
            img.RenderTransform = new TranslateTransform(-tx, -ty);
            return;
        }

        // 普通运镜（横向素材）：与导出一致——先按画布比例居中裁切，再在裁切框内缩放/移动焦点。
        // 坑：WPF Stretch=UniformToFill 的溢出内容锚定左上角、不居中，预览会偏向显示图片上部，
        // 与导出端的居中裁切不一致（人脸框、点击取景的画布坐标全按居中裁切计算）。
        // 改为显式 cover 布局：宿主撑到 cover 尺寸，MatrixTransform 平移+缩放到当前取景窗。
        var (offX, offY, bw, bh) = CoverMath.Cover(iw, ih, w, h);
        host.Width = bw;
        host.Height = bh;
        host.ClipToBounds = false;
        img.Stretch = Stretch.Fill;
        ResetImageLayout(img);
        var winW = w / z;
        var winH = h / z;
        var tx2 = Math.Clamp(st.FocusX * w - winW / 2, 0, Math.Max(0, w - winW));
        var ty2 = Math.Clamp(st.FocusY * h - winH / 2, 0, Math.Max(0, h - winH));
        img.RenderTransform = new MatrixTransform(z, 0, 0, z, -(offX + tx2) * z, -(offY + ty2) * z);
    }

    private void ApplyTransition(TransitionKind kind, double p)
    {
        var w = PreviewRoot.ActualWidth;
        var h = PreviewRoot.ActualHeight;

        switch (kind)
        {
            case TransitionKind.CrossFade:
                HostB.Opacity = p;
                break;

            case TransitionKind.FadeBlack:
                if (p < 0.5)
                {
                    HostA.Opacity = 1 - p * 2;
                    HostB.Opacity = 0;
                }
                else
                {
                    HostA.Opacity = 0.0001;
                    HostB.Opacity = (p - 0.5) * 2;
                }
                break;

            case TransitionKind.WipeLeft:
                HostB.Clip = new RectangleGeometry(new Rect(0, 0, w * p, h));
                break;

            case TransitionKind.WipeRight:
                HostB.Clip = new RectangleGeometry(new Rect(w * (1 - p), 0, w * p, h));
                break;

            case TransitionKind.SlideLeft:
                HostB.RenderTransform = new TranslateTransform(w * (1 - p), 0);
                break;

            case TransitionKind.SlideRight:
                HostB.RenderTransform = new TranslateTransform(-w * (1 - p), 0);
                break;

            case TransitionKind.CircleOpen:
                var r = Math.Sqrt(w * w + h * h) / 2 * p;
                HostB.Clip = new EllipseGeometry(new Point(w / 2, h / 2), r, r);
                break;

            default:
                HostB.Opacity = p;
                break;
        }
    }

    /// <summary>
    /// 在预览画面上用矩形圈出当前片段检测到的人脸。
    /// 矩形随当前运镜（缩放/平移/纵向滑动）同步移动，几何与 ApplyKenBurns 一致。
    /// </summary>
    private void UpdateFaceBox(Clip? clip)
    {
        var hide = () => FaceBox.Visibility = Visibility.Collapsed;

        if (clip is null || _playing || clip.Kind != MediaKind.Image ||
            !clip.FaceDetected || clip.FaceH <= 0.01)
        {
            hide();
            return;
        }

        var (idx, local) = _project.Locate(_time);
        if (idx < 0 || !ReferenceEquals(_project.Clips[idx], clip))
        {
            hide();
            return;
        }

        var w = PreviewRoot.ActualWidth;
        var h = PreviewRoot.ActualHeight;
        if (w < 4 || h < 4)
        {
            hide();
            return;
        }

        var p = clip.Duration <= 0.001 ? 0 : Math.Clamp(local / clip.Duration, 0, 1);
        var st = MotionMath.At(clip, p);
        var z = Math.Max(1.0, st.Zoom);

        // 人脸中心在预览上的屏幕坐标 + 当前显示缩放（与 ApplyKenBurns 的两条路径一致）
        double cx, cy, scale;
        if (clip.PanV == 1)
        {
            // 竖幅纵向平移：整图按 cover 尺寸铺开滑动。纵向用原图坐标 FaceSY 定位
            //（画布坐标 FaceY 只覆盖可见横带，会失真）；横向 cover 恰好铺满画布（tx=0）。
            var iw = clip.SourceWidth > 0 ? clip.SourceWidth : 1;
            var ih = clip.SourceHeight > 0 ? clip.SourceHeight : 1;
            var (dw, dh, tx, ty) = CoverMath.Window(iw, ih, w, h, 1.0, st.FocusX, st.FocusY);
            cx = clip.FaceX * w;
            cy = clip.FaceSY * dh - ty;
            scale = 1.0;
        }
        else
        {
            var winW = w / z;
            var winH = h / z;
            var tx = Math.Clamp(st.FocusX * w - winW / 2, 0, Math.Max(0, w - winW));
            var ty = Math.Clamp(st.FocusY * h - winH / 2, 0, Math.Max(0, h - winH));
            cx = (clip.FaceX * w - tx) * z;
            cy = (clip.FaceY * h - ty) * z;
            scale = z;
        }

        // FaceH 是含发型的头高（1.45× 检测框）；圈脸画的是检测框本身
        var boxH = clip.FaceH * h / 1.45 * scale;
        var boxW = boxH * 0.82;
        if (boxH < 6 || boxW < 6)
        {
            hide();
            return;
        }

        FaceBox.Width = boxW;
        FaceBox.Height = boxH;
        Canvas.SetLeft(FaceBox, cx - boxW / 2);
        Canvas.SetTop(FaceBox, cy - boxH / 2);
        FaceBox.Visibility = Visibility.Visible;
    }

    private BitmapSource? GetPreviewImage(Clip clip)
    {
        if (_previewCache.TryGet(clip.Id, out var cached)) return cached;
        if (_previewLoading.Contains(clip.Id)) return null;

        _previewLoading.Add(clip.Id);
        var path = clip.SourcePath;
        var id = clip.Id;

        _ = Task.Run(() =>
        {
            var bmp = LoadPhotoBitmap(path, 1280);
            if (bmp is null)
            {
                Dispatcher.BeginInvoke(() => _previewLoading.Remove(id));
                return;
            }
            bmp.Freeze();
            Dispatcher.BeginInvoke(() =>
            {
                _previewCache[id] = bmp;
                _previewLoading.Remove(id);
                RenderFrame();
            });
        });

        return null;
    }

    /// <summary>
    /// 解码整张照片（不解裁切）：竖幅照片的上下内容必须在，取景窗才能滑动。
    /// 按长边限制尺寸，够画布预览清晰即可。
    /// </summary>
    private static BitmapSource? LoadPhotoBitmap(string path, int maxLongSide)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(MediaSource.Resolve(path), UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            // 先按宽度限制；竖幅照片宽度通常远小于高度，解码后再按长边兜底
            bi.DecodePixelWidth = maxLongSide;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();
            // 常见坑：JPG 常带 72dpi 元数据，DIP 尺寸≠像素尺寸，会让 Image 的拉伸/裁切计算错乱。
            // 统一重铸为 96dpi（DIP=像素），后续几何全部按像素计算。
            if (Math.Abs(bi.DpiX - 96) > 0.1 || Math.Abs(bi.DpiY - 96) > 0.1)
            {
                var stride = (bi.PixelWidth * bi.Format.BitsPerPixel + 7) / 8;
                var buf = new byte[stride * bi.PixelHeight];
                bi.CopyPixels(buf, stride, 0);
                var normalized = BitmapSource.Create(bi.PixelWidth, bi.PixelHeight, 96, 96,
                    bi.Format, null, buf, stride);
                normalized.Freeze();
                return normalized;
            }
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureVideoBitmap(int w, int h)
    {
        if (_videoBitmap is not null && _videoBitmap.PixelWidth == w && _videoBitmap.PixelHeight == h) return;
        _videoBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
    }

    private void EnsureVideo(Clip clip, double local)
    {
        if (_scrubPreview) return;   // 暂停状态下走快速抓帧，不启动流式解码
        var target = clip.SourceIn + local;
        var reopen = _videoNeedsSeek
                     || _video.CurrentPath != clip.SourcePath
                     || (!_playing && Math.Abs(target - _videoOpenAt) > 0.12);

        if (!reopen) return;

        _video.Open(clip.SourcePath, target, _project.Fps);
        _videoOpenAt = target;
        _videoNeedsSeek = false;
    }

    // ==================================================================== 播放

    private void OnTogglePlay(object sender, RoutedEventArgs e) => TogglePlay();

    private void SetPlayIcon(bool playing)
        => PlayIcon.Data = (Geometry)FindResource(playing ? "IconPauseGeom" : "IconPlayGeom");

    private void TogglePlay()
    {
        if (_project.Clips.Count == 0) return;

        if (_playing)
        {
            PausePlayback();
            return;
        }

        if (_time >= _project.TotalDuration - 0.05) _time = 0;

        _playing = true;
        _scrubPreview = false;   // 播放切换回流式解码
        _playAnchor = _time;
        _clock.Restart();
        _videoNeedsSeek = true;
        SetPlayIcon(true);
        SetStatus("正在预览（预览不播放声音，声音在导出时合成）");
    }

    private void PausePlayback()
    {
        _playing = false;
        _scrubPreview = true;
        _clock.Stop();
        SetPlayIcon(false);
        _videoNeedsSeek = true;
        RenderFrame();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _scrubPreview = true;
        _clock.Stop();
        SetPlayIcon(false);
        _videoNeedsSeek = true;
        PreviewSeek(0);
        SetStatus("已回到开头");
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_playing) return;

        var t = _playAnchor + _clock.Elapsed.TotalSeconds;
        if (t >= _project.TotalDuration)
        {
            _time = _project.TotalDuration;
            _playing = false;
            _scrubPreview = true;
            _clock.Stop();
            SetPlayIcon(false);
            RenderFrame();
            UpdateTransportUi();
            SetStatus("预览结束");
            return;
        }

        _time = t;
        RenderFrame();
        UpdateTransportUi();
    }

    /// <summary>
    /// 暂停状态下的预览定位：拖动进度条 / 点击时间轴时实时显示对应画面。
    /// 视频片段用单帧快速抓取（OpenCV seek），不再每次拉起 ffmpeg 进程。
    /// </summary>
    private void PreviewSeek(double time)
    {
        if (_playing)
        {
            SeekTo(time);
            return;
        }

        _time = Math.Clamp(time, 0, Math.Max(0, _project.TotalDuration));
        _videoNeedsSeek = true;   // 之后按播放时从新位置起播

        var (idx, local) = _project.Locate(_time);
        if (idx < 0)
        {
            UpdateTransportUi();
            return;
        }

        RenderFrame();
        UpdateTransportUi();

        var clip = _project.Clips[idx];
        if (clip.Kind == MediaKind.Video && File.Exists(clip.SourcePath))
            RequestScrubFrame(clip, clip.SourceIn + local);
    }

    private void RequestScrubFrame(Clip clip, double srcTime)
    {
        _scrubWant = (clip.Id, srcTime);
        if (!_scrubBusy) _ = PumpScrubAsync();
    }

    private async Task PumpScrubAsync()
    {
        if (_scrubBusy) return;
        _scrubBusy = true;
        try
        {
            // 一次只处理最新请求；处理过程中来了新请求就取最新的
            while (_scrubWant is { } want && _scrubDone != want)
            {
                var clip = _project.Clips.FirstOrDefault(c => c.Id == want.ClipId);
                if (clip is null || !File.Exists(clip.SourcePath)) { _scrubDone = want; continue; }

                var data = await _grabber.GrabAsync(clip.SourcePath, want.SrcTime);
                _scrubDone = want;
                if (data is not null)
                {
                    _scrubFrameData = data;
                    _scrubFrameKey = want;
                    RenderFrame();   // await 回到 UI 线程，可直接渲染
                }
            }
        }
        finally
        {
            _scrubBusy = false;
        }
    }

    private void SeekTo(double time)
    {
        _time = Math.Clamp(time, 0, Math.Max(0, _project.TotalDuration));
        _videoNeedsSeek = true;

        if (_playing)
        {
            _playAnchor = _time;
            _clock.Restart();
        }

        RenderFrame();
        UpdateTransportUi();
    }

    private void OnSeekSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUi) return;
        PreviewSeek(e.NewValue);
    }

    private void UpdateTransportUi()
    {
        _suppressUi = true;
        try
        {
            if (SeekSlider.Maximum < _project.TotalDuration || SeekSlider.Maximum < 0.1)
                SeekSlider.Maximum = Math.Max(0.1, _project.TotalDuration);
            SeekSlider.Value = Math.Clamp(_time, 0, SeekSlider.Maximum);
        }
        finally
        {
            _suppressUi = false;
        }

        TxtTime.Text = $"{FormatTime(_time)} / {FormatTime(_project.TotalDuration)}";
        Timeline.Playhead = _time;
        EnsurePlayheadVisible();
        TxtTimelineInfo.Text = $"{_project.Clips.Count} 个片段 · {_project.TotalDuration:0.0}s";
    }

    /// <summary>
    /// 进度条拖动 / 播放时，让时间轴跟着定位并滚动，播放头始终留在视野里。
    /// 时间轴内部正在拖动片段时不做任何滚动——视口由拖动自身的边缘自动滚动接管，
    /// 否则会用旧播放头时间把视口拽回，长距离拖放后看不到落点。
    /// </summary>
    private void EnsurePlayheadVisible()
    {
        if (Timeline.IsDragging) return;

        var viewport = TimelineScroll.ViewportWidth;
        if (viewport <= 0) return;

        var x = _time * Timeline.PixelsPerSecond;
        var offset = TimelineScroll.HorizontalOffset;
        if (x < offset + 12 || x > offset + viewport - 12)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - viewport * 0.5));
    }

    private static string FormatTime(double t)
    {
        if (t < 0) t = 0;
        var ts = TimeSpan.FromSeconds(t);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    // ==================================================================== 预览交互：手动指定焦点

    private bool _focusDragging;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selected is null || _selected.Kind != MediaKind.Image) return;
        _focusDragging = true;
        PreviewRoot.CaptureMouse();
        SetFocusFromPoint(e.GetPosition(PreviewRoot));
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_focusDragging || e.LeftButton != MouseButtonState.Pressed) return;
        SetFocusFromPoint(e.GetPosition(PreviewRoot));
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_focusDragging) return;
        _focusDragging = false;
        PreviewRoot.ReleaseMouseCapture();
        SetStatus("已设定取景中心（按素材自动取景）");
    }

    private void SetFocusFromPoint(Point pt)
    {
        if (_selected is null) return;
        var w = PreviewRoot.ActualWidth;
        var h = PreviewRoot.ActualHeight;
        if (w < 4 || h < 4) return;

        var (idx, local) = _project.Locate(_time);
        if (idx < 0 || !ReferenceEquals(_project.Clips[idx], _selected)) return;

        var p = _selected.Duration <= 0.001 ? 0 : Math.Clamp(local / _selected.Duration, 0, 1);
        var st = MotionMath.At(_selected, p);
        var z = Math.Max(1.0, st.Zoom);
        var winW = w / z;
        var winH = h / z;
        var tx = Math.Clamp(st.FocusX * w - winW / 2, 0, Math.Max(0, w - winW));
        var ty = Math.Clamp(st.FocusY * h - winH / 2, 0, Math.Max(0, h - winH));

        var canvasX = pt.X / z + tx;
        var canvasY = pt.Y / z + ty;

        _selected.FaceX = Math.Clamp(canvasX / w, 0, 1);
        _selected.FaceY = Math.Clamp(canvasY / h, 0, 1);

        // 竖幅照片的取景用的是原图坐标：把点击位置换算回原图 Y，否则竖幅片段会忽略这次点击
        var iw = _selected.SourceWidth;
        var ih = _selected.SourceHeight;
        if (iw > 0 && ih > 0 && ih > iw)
        {
            var (_, offY, _, bh) = CoverMath.Cover(iw, ih, _project.Width, _project.Height);
            var iy = (canvasY / h * _project.Height + offY) / bh;
            _selected.FaceSY = Math.Clamp(iy, 0.01, 0.99);
        }

        MotionPresets.Apply(_selected, _project.Clips.IndexOf(_selected), _project.Width, _project.Height);

        RefreshInspector();
        RenderFrame();
    }

    // ==================================================================== 缩放

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        Timeline.PixelsPerSecond *= 1.4;
        Timeline.Refresh();
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        Timeline.PixelsPerSecond /= 1.4;
        Timeline.Refresh();
    }

    // ==================================================================== 工程

    /// <summary>给工程挂上集合变更事件，并把旧版配乐迁移成 MusicPaths。</summary>
    private void AttachProjectEvents(ProjectFile p, string? projectPath = null)
    {
        p.Clips.CollectionChanged += (_, _) => RefreshAfterTimelineEdit();
        p.MigrateLegacyMusic();

        // 相对路径的工程统一解析为绝对路径：优先按工程文件所在目录，
        // 文件不存在再按进程工作目录（兼容旧工具按 CWD 保存的工程）。
        // 否则 WPF 解码器（UriKind.Absolute）和 ffmpeg 对路径的解析会不一致。
        var baseDir = projectPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectPath));
        void FixPath(Func<string> get, Action<string> set)
        {
            var v = get();
            if (string.IsNullOrEmpty(v) || Path.IsPathRooted(v)) return;
            var byProject = baseDir is null ? null : Path.GetFullPath(Path.Combine(baseDir, v));
            if (byProject is not null && File.Exists(byProject)) { set(byProject); return; }
            set(Path.GetFullPath(v));
        }
        foreach (var c in p.Clips)
            FixPath(() => c.SourcePath, v => c.SourcePath = v);
        for (var i = 0; i < p.MusicPaths.Count; i++)
        {
            var idx = i;
            FixPath(() => p.MusicPaths[idx], v => p.MusicPaths[idx] = v);
        }
    }

    private void ApplyProjectToUi()
    {
        Timeline.Project = _project;
        Timeline.Refresh();
        TxtResBadge.Text = $"{_project.Width}×{_project.Height} · {_project.Fps}fps";
        _project.MigrateLegacyMusic();
        RefreshAfterTimelineEdit();
        RefreshInspector();
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        if (_project.Clips.Count > 0)
        {
            var r = MessageBox.Show(this, "新建会清空当前时间轴，继续吗？", "新建项目",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }

        _project = new ProjectFile();
        AttachProjectEvents(_project);
        _selected = null;
        _projectPath = null;
        _time = 0;
        _previewCache.Clear();
        _previewLoading.Clear();
        _video.Close();
        ApplyProjectToUi();
        RenderFrame();
        MarkClean();
        SetStatus("已新建项目");
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "打开工程", Filter = "Memories Studio 工程|*.msproj|所有文件|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        _ = OpenProjectFromPathAsync(dlg.FileName);
    }

    /// <summary>
    /// 打开工程文件。菜单「打开工程」与双击 .msproj（文件关联）共用此入口。
    /// 返回是否成功载入。
    /// </summary>
    public async Task<bool> OpenProjectFromPathAsync(string path, bool confirmDiscard = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "工程文件不存在：\n" + path, "打开失败");
            return false;
        }

        var loaded = ProjectStore.Load(path);
        if (loaded is null)
        {
            MessageBox.Show(this, "工程文件无法读取。", "打开失败");
            return false;
        }

        if (confirmDiscard && _dirty)
        {
            var r = MessageBox.Show(this,
                "当前工程有未保存的修改，仍要打开其他工程吗？", "打开工程",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return false;
        }

        _project = loaded;
        AttachProjectEvents(_project, path);
        _selected = null;
        _projectPath = path;
        _time = 0;
        _previewCache.Clear();
        _previewLoading.Clear();
        _video.Close();
        ApplyProjectToUi();
        RenderFrame();
        MarkClean();
        SetStatus($"已打开工程：{Path.GetFileName(path)}");

        // 从片段与配乐重建素材库，并把视频片段的时长规范化为素材实际长度
        await InitializeProjectAssetsAsync();
        return true;
    }

    private async Task RebuildAssetsFromProjectAsync()
    {
        // 动图片段的 SourcePath 是缓存截帧 JPG，素材库应还原成原始视频（StillFrom），
        // 否则会多出一堆哈希命名的"图片"素材
        var paths = _project.Clips
            .Select(c => string.IsNullOrEmpty(c.StillFrom) ? c.SourcePath : c.StillFrom)
            .ToList();
        paths.AddRange(_project.MusicPaths);
        paths = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        if (paths.Count == 0) return;

        await ImportFilesAsync(paths, healthCheck: false);
        var missing = paths.Count(p => !File.Exists(p));
        if (missing > 0)
            SetStatus($"工程已打开，但有 {missing} 个素材文件不在原位置（已移动或删除），预览与导出会跳过它们。");
    }

    /// <summary>打开工程后的初始化：重建素材库 + 视频片段时长规范化 + 旧工程图片片段补人脸检测。</summary>
    private async Task InitializeProjectAssetsAsync()
    {
        await RebuildAssetsFromProjectAsync();
        await NormalizeVideoClipsAsync();

        // 动图片段的截帧缓存可能被清理（或换了机器）：用原始视频重新截一帧。
        // 截帧是同步 ffmpeg 调用，放后台；属性回填必须回 UI 线程（绑定更新）。
        var stale = _project.Clips.Where(c => c.Kind == MediaKind.Image
                                              && !string.IsNullOrEmpty(c.StillFrom)
                                              && !File.Exists(c.SourcePath)).ToList();
        if (stale.Count > 0)
        {
            var rebuilt = await Task.Run(() => MediaSource.RebuildMissingStills(stale));
            if (rebuilt.Count > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (clip, still) in rebuilt)
                    {
                        clip.SourcePath = still;
                        clip.ThumbnailPath = still;
                    }
                    Timeline.Refresh();
                    RenderFrame();
                });
                SetStatus($"已为 {rebuilt.Count} 个动图重新截帧（缓存文件被清理过）。");
            }
        }

        // 旧工程的图片片段检测数据过时（FaceHV<2：Haar 时代误检多、框偏移）或缺失 →
        // 用 YuNet 重新检测，让取景对新老工程都准确生效（后台执行，不阻塞打开工程）
        foreach (var c in _project.Clips
                     .Where(c => c.Kind == MediaKind.Image && c.FaceHV < 2 && File.Exists(c.SourcePath))
                     .ToList())
        {
            _ = DetectFaceAsync(c);
        }

        // 旧运镜数据（MotionV<3）→ 按"按素材自动取景"重新生成
        var upgraded = 0;
        foreach (var c in _project.Clips.ToList())
        {
            if (c.Kind != MediaKind.Image || c.MotionV >= 3) continue;
            MotionPresets.Apply(c, _project.Clips.IndexOf(c), _project.Width, _project.Height);
            upgraded++;
        }
        if (upgraded > 0)
        {
            Timeline.Refresh();
            RenderFrame();
        }
    }

    /// <summary>
    /// 把工程里的视频片段与素材真实时长对齐：
    /// 旧版本工程把视频一律存成 4 秒默认值——这里重新探测素材，恢复为实际时长；
    /// 只有入点为 0 且时长仍是旧默认值（4s / 5s）时才重置，用户手动裁剪过的保持不变。
    /// </summary>
    private async Task NormalizeVideoClipsAsync()
    {
        var videoClips = _project.Clips
            .Where(c => c.Kind == MediaKind.Video && File.Exists(c.SourcePath))
            .DistinctBy(c => c.SourcePath)
            .ToList();
        if (videoClips.Count == 0) return;

        var fixedCount = 0;
        foreach (var clip in videoClips)
        {
            var probe = await MediaProbe.ProbeAsync(clip.SourcePath);
            if (probe is null || probe.Duration <= 0.2) continue;

            await Dispatcher.InvokeAsync(() =>
            {
                ApplyProbeToClip(clip, probe);
                var wasDefault = IsLegacyDefaultDuration(clip);
                if (wasDefault)
                {
                    clip.Duration = Math.Max(0.5, probe.Duration - clip.SourceIn);
                    fixedCount++;
                }
                else
                {
                    // 用户裁剪过：夹取到素材实际范围
                    clip.Duration = Math.Clamp(clip.Duration, 0.5,
                        Math.Max(0.5, probe.Duration - clip.SourceIn));
                }
            });
        }

        await Dispatcher.InvokeAsync(() =>
        {
            RefreshAfterTimelineEdit();
            RefreshInspector();
            RenderFrame();
            if (fixedCount > 0)
                SetStatus($"已把 {fixedCount} 个视频片段的时长恢复为素材实际长度（旧工程默认 4 秒已修正）");
        });
    }

    private static bool IsLegacyDefaultDuration(Clip c)
        => c.SourceIn <= 0.001 && (Math.Abs(c.Duration - 4.0) < 0.06 || Math.Abs(c.Duration - 5.0) < 0.06);

    /// <summary>把探测结果写入片段（分辨率 / 原声 / 素材总时长）。</summary>
    private static void ApplyProbeToClip(Clip clip, ProbeResult probe)
    {
        if (probe.Width > 0)
        {
            clip.SourceWidth = probe.Width;
            clip.SourceHeight = probe.Height;
        }
        clip.HasAudio = probe.HasAudio;
        if (probe.Duration > 0.2) clip.SourceDuration = probe.Duration;
    }

    private void OnSaveProject(object sender, RoutedEventArgs e) => SaveProject();

    /// <summary>保存工程。返回是否保存成功（用户取消另存对话框或保存失败返回 false）。</summary>
    private bool SaveProject()
    {
        var path = _projectPath;
        if (string.IsNullOrEmpty(path))
        {
            var dlg = new SaveFileDialog { Title = "保存工程", Filter = "Memories Studio 工程|*.msproj", FileName = "我的记忆.msproj" };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }

        try
        {
            ProjectStore.Save(_project, path);
            _projectPath = path;
            MarkClean();
            SetStatus($"工程已保存：{path}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "错误");
            return false;
        }
    }

    // ==================================================================== 导出

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_project.Clips.Count == 0)
        {
            MessageBox.Show(this, "时间轴里还没有片段，先加入一些素材吧。", "提示");
            return;
        }

        if (_playing) PausePlayback();

        var win = new ExportWindow(_project) { Owner = this };
        win.ShowDialog();
    }

    // ==================================================================== 导入自检（--import-test）

    public int AssetsCount => _assets.Count;
    public int ClipsCount => _project.Clips.Count;

    /// <summary>自检访问器：素材库列表（核对导入后的类型/路径）。</summary>
    public IReadOnlyList<MediaAsset> AssetsForTest => _assets.ToList();
    public System.Collections.ObjectModel.ObservableCollection<Clip> Clips => _project.Clips;
    public double TotalDuration => _project.TotalDuration;

    /// <summary>自动化测试模式下跳过"未保存更改"关闭确认。</summary>
    public bool SuppressCloseConfirm { get; set; }

    /// <summary>自动化自检：模拟用户导入素材 → 加入时间轴 → 预览渲染的全过程。</summary>
    public async Task ImportForTest(string path, string? saveTo = null)
    {
        await ImportFilesAsync(new[] { path });
        await Task.Delay(1500);          // 等模板实例化

        // 加入时间轴（内部会等待所有素材探测完成），真正等待其结束
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var assets = _assets.ToList();
                if (assets.Count > 0) await AddAssetsToTimelineAsync(assets);
            }
            finally { tcs.TrySetResult(null); }
        }, System.Windows.Threading.DispatcherPriority.Background);
        await tcs.Task;

        // 真等全部图片片段的人脸检测结束（检测队列串行，2 并发），最多 60 秒
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline &&
               _project.Clips.Any(c => c.Kind == MediaKind.Image && c.FaceHV < 2))
        {
            await Task.Delay(200);
        }

        await Task.Delay(800);           // 等预览缓存 / 时间轴重绘

        if (!string.IsNullOrWhiteSpace(saveTo))
        {
            _projectPath = saveTo;
            ProjectStore.Save(_project, saveTo);   // 供 --export 直接验证导出链路
            _dirty = false;
        }
    }

    /// <summary>自动化自检：打开既有工程 → 重建素材库 → 验证时间轴与预览。</summary>
    public async Task OpenProjectForTest(string path)
    {
        var loaded = ProjectStore.Load(path);
        if (loaded is null) throw new InvalidOperationException("工程文件无法读取: " + path);

        _project = loaded;
        AttachProjectEvents(_project, path);
        _selected = null;
        _projectPath = path;
        _time = 0;
        _previewCache.Clear();
        _previewLoading.Clear();
        _video.Close();
        ApplyProjectToUi();
        RenderFrame();

        await InitializeProjectAssetsAsync();
        await Task.Delay(4000);          // 等探测 / 缩略图 / 预览封面加载
        // 选中第一个视频片段，让属性面板（含节选输入框）进入截图验证范围
        await Dispatcher.InvokeAsync(() =>
        {
            _selected = _project.Clips.FirstOrDefault(c => c.Kind == MediaKind.Video);
            if (_selected is not null) Timeline.SetClipSelection(new[] { _selected }, _selected);
            RefreshInspector();
        });
        // 触发一次重绘，确认预览封面已进入缓存
        _time = Math.Min(0.5, _project.TotalDuration * 0.1);
        RenderFrame();
        await Task.Delay(1800);
    }

    /// <summary>自检访问器：当前工程。</summary>
    public Models.ProjectFile? ProjectForTest => _project;

    /// <summary>
    /// 自检：选中时间轴第 index 个片段（1 起），验证素材库自动跟随定位的效果，
    /// 返回素材在素材库视口中的纵向位置（应接近 viewport 的一半），并整窗截图。
    /// </summary>
    public string RevealForTest(int index1, string outPath)
    {
        if (_project.Clips.Count == 0) return "FAIL no-clips";
        var i = Math.Clamp(index1 - 1, 0, _project.Clips.Count - 1);
        var clip = _project.Clips[i];

        // 与鼠标点击时间轴走同一套逻辑：选中 → 刷新属性/预览 → 素材库定位
        Timeline.SetClipSelection(new[] { clip }, clip);
        OnTimelineSelectionChanged(Timeline, clip);
        UpdateLayout();

        var path = string.IsNullOrEmpty(clip.StillFrom) ? clip.SourcePath : clip.StillFrom;
        var asset = _assets.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        var sv = FindDescendant<ScrollViewer>(AssetsList);
        var pos = "-";
        if (asset is not null && sv is not null
            && AssetsList.ItemContainerGenerator.ContainerFromItem(asset) is ListBoxItem c)
        {
            var y = c.TranslatePoint(new Point(0, c.ActualHeight / 2), sv).Y;
            pos = $"{y:0.0}";
        }

        var w = (int)Math.Max(2, ActualWidth);
        var h = (int)Math.Max(2, ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        // 模拟光标键：→ 右滚时间轴 ×3、↓ 下滚素材库 ×2，验证窗口级键盘路由生效
        var h0 = TimelineScroll.HorizontalOffset;
        var a0 = FindDescendant<ScrollViewer>(AssetsList)?.VerticalOffset ?? -1;
        SendTestKey(Key.Right); SendTestKey(Key.Right); SendTestKey(Key.Right);
        SendTestKey(Key.Down); SendTestKey(Key.Down);
        UpdateLayout();
        var h1 = TimelineScroll.HorizontalOffset;
        var a1 = FindDescendant<ScrollViewer>(AssetsList)?.VerticalOffset ?? -1;

        return $"OK index={i + 1} clip={clip.FileName} asset={asset?.FileName ?? "-"} " +
               $"offset={sv?.VerticalOffset:0.0} viewport={sv?.ViewportHeight:0.0} " +
               $"extent={sv?.ExtentHeight:0.0} listH={AssetsList.ActualHeight:0.0} " +
               $"centerY={pos} selected={AssetsList.SelectedItems.Count} " +
               $"keys tl {h0:0.0}->{h1:0.0} assets {a0:0.0}->{a1:0.0}";
    }

    private void SendTestKey(Key key)
    {
        var source = PresentationSource.FromVisual(this);
        if (source is null) return;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        RaiseEvent(args);
    }

    /// <summary>自检访问器：时间轴控件。</summary>
    public Controls.TimelineControl TimelineForTest => Timeline;

    /// <summary>自检：选中第一个视频片段，把整窗渲染成 PNG，用于核对右侧属性面板排版。</summary>
    public string InspectorForTest(string outPath)
    {
        var clip = _project.Clips.FirstOrDefault(c => c.Kind == MediaKind.Video);
        if (clip is null)
        {
            // 纯图片工程（如动图按图片入轨）：不选片段，整窗截图核对时间轴预览
            UpdateLayout();
            var w0 = (int)Math.Max(2, ActualWidth);
            var h0 = (int)Math.Max(2, ActualHeight);
            var rtb0 = new RenderTargetBitmap(w0, h0, 96, 96, PixelFormats.Pbgra32);
            rtb0.Render(this);
            var enc0 = new PngBitmapEncoder();
            enc0.Frames.Add(BitmapFrame.Create(rtb0));
            using (var fs = File.Create(outPath)) enc0.Save(fs);
            var withThumb = _project.Clips.Count(c =>
                GetThumbnailForTimeline(c) is not null);
            return $"OK no-video-clips thumbs={withThumb}/{_project.Clips.Count}";
        }

        Timeline.SetClipSelection(new[] { clip }, clip);
        _selected = clip;
        RefreshInspector();
        UpdateLayout();

        var w = (int)Math.Max(2, ActualWidth);
        var h = (int)Math.Max(2, ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        return $"OK clip={clip.FileName} dur={clip.Duration:0.0} srcDur={clip.SourceDuration:0.0} " +
               $"durPanel={DurPanel.Visibility} trimPanel={TrimPanel.Visibility} " +
               $"srcLen='{TxtTrimSrcLen.Text}' len='{TxtTrimLen.Text}'";
    }

    /// <summary>自检：跳到指定时间，把预览区真实渲染结果存成 PNG，并返回当前取景几何参数。</summary>
    public async Task<string> SnapshotForTest(double time, string outPath)
    {
        SeekTo(time);
        RenderFrame();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        await Task.Delay(1800);                     // 等预览位图解码回填
        RenderFrame();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        await Task.Delay(200);
        RenderFrame();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        // 强制完整走一遍 测量/排列/渲染 管线后再捕获，避免 RTB 拿到陈旧组合帧
        PreviewRoot.InvalidateMeasure();
        PreviewRoot.InvalidateVisual();
        PreviewRoot.UpdateLayout();
        RenderFrame();
        HostA.InvalidateVisual();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        var w = (int)Math.Max(2, Math.Round(PreviewRoot.ActualWidth));
        var h = (int)Math.Max(2, Math.Round(PreviewRoot.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(PreviewRoot);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        // HostA 单独渲染：定位 458 裁剪发生在哪一层
        var hostPng = Path.Combine(
            Path.GetDirectoryName(outPath) ?? ".",
            Path.GetFileNameWithoutExtension(outPath) + "_host.png");
        var rtbH = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtbH.Render(HostA);
        var encH = new PngBitmapEncoder();
        encH.Frames.Add(BitmapFrame.Create(rtbH));
        using (var fs = File.Create(hostPng)) encH.Save(fs);

        // ImgA 全身渲染：临时清变换，看 Image 元素全高里实际画了什么（位图未加载时跳过）
        if (ImgA.ActualWidth >= 2 && ImgA.ActualHeight >= 2)
        {
            var imgPng = Path.Combine(
                Path.GetDirectoryName(outPath) ?? ".",
                Path.GetFileNameWithoutExtension(outPath) + "_img.png");
            var savedTf = ImgA.RenderTransform;
            ImgA.RenderTransform = Transform.Identity;
            ImgA.UpdateLayout();
            var rtbI = new RenderTargetBitmap((int)ImgA.ActualWidth, (int)ImgA.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtbI.Render(ImgA);
            ImgA.RenderTransform = savedTf;
            ImgA.UpdateLayout();
            var encI = new PngBitmapEncoder();
            encI.Frames.Add(BitmapFrame.Create(rtbI));
            using (var fs = File.Create(imgPng)) encI.Save(fs);
        }

        // 源位图 DPI 信息
        var srcInfo = "";
        if (ImgA.Source is BitmapSource bs)
            srcInfo = $" srcPx={bs.PixelWidth}x{bs.PixelHeight} dpiX={bs.DpiX:0.0} dpiY={bs.DpiY:0.0} dipH={bs.Height:0.0}";

        // 同时渲染整个窗口，便于对比预览区在真实窗口中的样子
        var winPng = Path.Combine(
            Path.GetDirectoryName(outPath) ?? ".",
            Path.GetFileNameWithoutExtension(outPath) + "_window.png");
        var ww = (int)Math.Max(2, ActualWidth);
        var wh = (int)Math.Max(2, ActualHeight);
        var rtb2 = new RenderTargetBitmap(ww, wh, 96, 96, PixelFormats.Pbgra32);
        rtb2.Render(this);
        var enc2 = new PngBitmapEncoder();
        enc2.Frames.Add(BitmapFrame.Create(rtb2));
        using (var fs = File.Create(winPng)) enc2.Save(fs);

        var (idx, local) = _project.Locate(_time);
        if (idx < 0) return "no-clip";
        var c = _project.Clips[idx];
        var p = c.Duration <= 0.001 ? 0 : Math.Clamp(local / c.Duration, 0, 1);
        var st = MotionMath.At(c, p);
        var bmp = ImgA.Source as BitmapSource;
        var tf = ImgA.RenderTransform as TranslateTransform;
        var tfm = ImgA.RenderTransform as MatrixTransform;
        var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(ImgA);
        var bounds = VisualTreeHelper.GetDescendantBounds(ImgA);
        var hostSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(HostA);

        // 渲染时的真实几何：元素到 PreviewRoot 的完整变换链 + 实际渲染尺寸
        var mat = TransformToRoot(ImgA, PreviewRoot);
        var matHost = TransformToRoot(HostA, PreviewRoot);
        var prT = TransformToRoot(PreviewRoot, (Visual)VisualTreeHelper.GetParent(PreviewRoot) is Visual pv ? pv : PreviewRoot);
        var geo = $" IMG2ROOT=[{mat.M11:0.00},{mat.M12:0.00};{mat.M21:0.00},{mat.M22:0.00};{mat.OffsetX:0.0},{mat.OffsetY:0.0}]" +
                  $" HOST2ROOT=[{matHost.M11:0.00},{matHost.M12:0.00};{matHost.M21:0.00},{matHost.M22:0.00};{matHost.OffsetX:0.0},{matHost.OffsetY:0.0}]" +
                  $" IMG.ActualH={ImgA.ActualHeight:0.0} IMG.SrcH={bmp?.Height:0.0}" +
                  $" HostA.TF={(HostA.RenderTransform is Transform ht ? ht.Value.ToString() : "null")}" +
                  $" PreviewRoot.TF={(PreviewRoot.RenderTransform is Transform pt ? pt.Value.ToString() : "null")}" +
                  $" PreviewRoot.Offset={prT.OffsetX:0.0},{prT.OffsetY:0.0}" +
                  $" ImgA.RenderSize={ImgA.RenderSize.Width:0.0}x{ImgA.RenderSize.Height:0.0}" +
                  $" ImgA.Desired={ImgA.DesiredSize.Width:0.0}x{ImgA.DesiredSize.Height:0.0}" +
                  $" HostA.RenderSize={HostA.RenderSize.Width:0.0}x{HostA.RenderSize.Height:0.0}" +
                  $" ImgA.ClipTB={ImgA.ClipToBounds} ImgA.Clip={(ImgA.Clip is null ? "null" : ImgA.Clip.Bounds.ToString())}" +
                  $" HostA.ClipTB={HostA.ClipToBounds} PR.ClipTB={PreviewRoot.ClipToBounds}" +
                  $" ImgA.MaxH={ImgA.MaxHeight:0.0} ImgA.MinH={ImgA.MinHeight:0.0}" + srcInfo;

        // 遍历预览区视觉树，列出每个元素的类型/名称/有效边界/不透明度
        var tree = new System.Text.StringBuilder();
        void Walk(DependencyObject node, int depth)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Visual v)
                {
                    var b = VisualTreeHelper.GetDescendantBounds(v);
                    var fe = child as FrameworkElement;
                    tree.Append($" | d{depth} {child.GetType().Name}({fe?.Name}) vis={fe?.Visibility} op={(fe?.Opacity ?? 1):0.00} " +
                                $"bounds={b.X:0.0},{b.Y:0.0} {b.Width:0.0}x{b.Height:0.0}");
                }
                Walk(child, depth + 1);
            }
        }
        Walk(PreviewRoot, 0);
        return $"clip={Path.GetFileName(c.SourcePath)} idx={idx} local={local:0.00}/{c.Duration:0.00} " +
               $"src={c.SourceWidth}x{c.SourceHeight} bmp={bmp?.PixelWidth}x{bmp?.PixelHeight} " +
               $"pan={c.PanV} z={st.Zoom:0.000} fx={st.FocusX:0.000} fy={st.FocusY:0.000} " +
               $"z0={c.StartZoom:0.000} z1={c.EndZoom:0.000} fy0={c.StartFocusY:0.000} fy1={c.EndFocusY:0.000} " +
               $"rootW={PreviewRoot.Width} rootH={PreviewRoot.Height} actual={PreviewRoot.ActualWidth:0.0}x{PreviewRoot.ActualHeight:0.0} " +
               $"imgW={ImgA.Width:0.0} imgH={ImgA.Height:0.0} " +
               $"align={ImgA.HorizontalAlignment}/{ImgA.VerticalAlignment} stretch={ImgA.Stretch} " +
               $"slot={slot.Width:0.0}x{slot.Height:0.0}@({slot.X:0.0},{slot.Y:0.0}) " +
               $"hostSlot={hostSlot.Width:0.0}x{hostSlot.Height:0.0} " +
               $"imgBounds={bounds.Width:0.0}x{bounds.Height:0.0}@({bounds.X:0.0},{bounds.Y:0.0}) " +
               $"translate={(tf is null ? "-" : $"{tf.X:0.0},{tf.Y:0.0}")} " +
               $"matrix={(tfm is null ? "-" : $"{tfm.Matrix.M11:0.00},{tfm.Matrix.OffsetX:0.0},{tfm.Matrix.OffsetY:0.0}")} " +
               $"hostVis={HostB.Visibility} dpi={VisualTreeHelper.GetDpi(this).DpiScaleX:0.00}" +
               geo +
               $" TREE{tree}";
    }

    /// <summary>取 visual 到 ancestor 的累计变换（含布局偏移与 RenderTransform）。</summary>
    private static Matrix TransformToRoot(Visual visual, Visual ancestor)
    {
        var m = Matrix.Identity;
        Visual? cur = visual;
        while (cur is not null && !ReferenceEquals(cur, ancestor))
        {
            var rel = cur.TransformToAncestor((Visual)VisualTreeHelper.GetParent(cur)!);
            Matrix t;
            if (rel is MatrixTransform mt) t = mt.Matrix;
            else { var p = new Point(0, 0); var q = new Point(1, 1); var a = rel.Transform(p); var b = rel.Transform(q); t = Matrix.Identity; t.M11 = b.X - a.X; t.M22 = b.Y - a.Y; t.OffsetX = a.X; t.OffsetY = a.Y; }
            m = Matrix.Multiply(m, t);
            cur = VisualTreeHelper.GetParent(cur) as Visual;
        }
        return m;
    }

    // ==================================================================== 杂项

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var inText = Keyboard.FocusedElement is TextBox;

        if (e.Key == Key.Space && !inText)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !inText)
        {
            if (AssetsList.IsKeyboardFocusWithin) DeleteSelectedAssets();
            else if (Timeline.SelectedClips.Count > 0) RemoveClips(Timeline.SelectedClips.ToList());
            else if (_selected is not null) RemoveClips(new[] { _selected });
            e.Handled = true;
        }
        else if (!inText && Timeline.SelectedClips.Count > 0 && !AssetsList.IsKeyboardFocusWithin
                 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            // 时间轴上有选中片段时：左右滚动时间轴视口，上下逐行滚动素材库
            var page = Math.Max(80, TimelineScroll.ViewportWidth * 0.12);
            switch (e.Key)
            {
                case Key.Left: ScrollTimelineBy(-page); break;
                case Key.Right: ScrollTimelineBy(+page); break;
                case Key.Up: ScrollAssetsBy(-1); break;
                case Key.Down: ScrollAssetsBy(+1); break;
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// 删除素材库里选中的素材（Ctrl 多选）。仍被视频轨道或音频轨引用的不允许删，
    /// 提示用户先去轨道上删掉，避免工程里留下指向已删文件的空片段。
    /// </summary>
    private void DeleteSelectedAssets()
    {
        var selected = AssetsList.SelectedItems.Cast<MediaAsset>().ToList();
        if (selected.Count == 0) return;

        var inUse = new List<string>();
        var removed = 0;

        foreach (var a in selected)
        {
            var byClip = _project.Clips.Any(c =>
                string.Equals(c.SourcePath, a.Path, StringComparison.OrdinalIgnoreCase));
            if (byClip)
            {
                inUse.Add($"· {a.FileName}　视频轨道在用");
                continue;
            }

            _assets.Remove(a);
            removed++;
        }

        if (inUse.Count > 0)
        {
            MessageBox.Show(this,
                "以下素材正在时间轴中使用，请先在轨道上删除它们，再从素材库删除：\n\n" +
                string.Join("\n", inUse.Take(12)) +
                (inUse.Count > 12 ? $"\n… 共 {inUse.Count} 项在用" : ""),
                "素材正在使用中", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        SetStatus(removed > 0
            ? $"已从素材库删除 {removed} 项" + (inUse.Count > 0 ? $"（{inUse.Count} 项在用未删）" : "")
            : "选中的素材正在使用中，不能删除");
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    /// <summary>标记工程有未保存的修改（标题栏加 * 提示）。</summary>
    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void MarkClean()
    {
        _dirty = false;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = string.IsNullOrEmpty(_projectPath) ? "未命名项目" : Path.GetFileNameWithoutExtension(_projectPath);
        Title = $"Memories Studio — {name}" + (_dirty ? " *" : "");
    }

    private BitmapSource? GetThumbnailForTimeline(Clip clip)
    {
        // 优先取素材库的缩略图（异步生成完成后会回填），片段自身的作为兜底
        var path = _assets.FirstOrDefault(a => string.Equals(a.Path, clip.SourcePath,
                       StringComparison.OrdinalIgnoreCase))?.ThumbnailPath;
        if (string.IsNullOrEmpty(path))
            path = clip.ThumbnailPath;

        // 图片片段兜底：ThumbnailPath 不随工程保存（JsonIgnore），且动图片段的
        // SourcePath（截帧 JPG）与素材库路径（原始视频）对不上、无法回填；
        // SourcePath 本身就是图片文件，直接用它作预览。
        if (string.IsNullOrEmpty(path) && clip.Kind == MediaKind.Image && File.Exists(clip.SourcePath))
            path = clip.SourcePath;

        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (_thumbCache.TryGet(path, out var cached)) return cached;

        // 按需加载：不在 UI 线程同步解码（上千素材首次绘制会卡死界面）。
        // 时间轴只绘制视口附近的片段，所以未滚到的图永远不会被解码；
        // 解码完成后回填 LRU 并请求重绘。请求去重靠 _thumbPending。
        if (_thumbPending.Add(path))
        {
            var p = path;
            _ = Task.Run(() =>
            {
                BitmapSource? bmp = null;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(p, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 220;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.EndInit();
                    bi.Freeze();
                    bmp = bi;
                }
                catch { }
                Dispatcher.BeginInvoke(() =>
                {
                    _thumbPending.Remove(p);
                    if (bmp is not null)
                    {
                        _thumbCache[p] = bmp;
                        Timeline.InvalidateVisual();
                    }
                });
            });
        }
        return null;
    }

    private void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 有未保存的更改：询问 保存 / 不保存 / 取消（自动化测试模式跳过）
        if (_dirty && !SuppressCloseConfirm)
        {
            var r = MessageBox.Show(this, "当前项目有未保存的更改，要保存吗？", "关闭 Memories Studio",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (r == MessageBoxResult.Yes && !SaveProject())
            {
                e.Cancel = true;    // 保存被取消或失败 → 不关闭
                return;
            }
        }

        CompositionTarget.Rendering -= OnRendering;
        try
        {
            _video.Close();
            _grabber.Dispose();
        }
        catch { }
    }
}
