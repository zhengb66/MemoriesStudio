using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MemoriesStudio.Models;

namespace MemoriesStudio.Controls;

/// <summary>
/// 自绘时间轴。相比用 ItemsControl 堆控件，自绘在几十上百个片段时依然不卡：
/// 只有一次 OnRender，没有布局系统和可视化树的反复开销。
/// 支持 Ctrl 多选片段、按住片段横向拖动调整顺序（连续多选整块移动）。
/// 0.3 起移除了音频轨（配乐改在全局设置里管理，导出时合成）。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerHeight = 22;
    private const double ClipTop = 27;
    private const double ClipHeight = 92;
    private const double DragThreshold = 6;   // 按下片段后超过该位移才进入拖动排序

    private enum PressMode { None, Scrub, MaybeDragClip, DragClips }

    private readonly Typeface _typeface = new("Microsoft YaHei UI");
    private PressMode _press = PressMode.None;
    private Point _pressPos;
    private Clip? _pressClip;
    private Clip? _hover;
    private Clip? _anchorClip;   // Shift 连选的锚点（上一次单选/首选的片段）

    // 拖动排序状态
    private System.Collections.Generic.List<Clip> _dragBlock = new();
    private int _dropIndex = -1;      // 插入位置（剩余片段列表中的下标）
    private double _caretX;           // 插入指示线位置（像素）

    // 拖动辅助：边缘自动滚动 + 数量角标光标
    private System.Windows.Threading.DispatcherTimer? _autoScrollTimer;
    private DateTime _dragSettleUntil = DateTime.MinValue;   // 落手后的静默期，期间不自动滚动视口
    private readonly System.Collections.Generic.Dictionary<(int Count, bool Video), Cursor> _cursorCache = new();

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        AllowDrop = true;

        // 进可视树后挂接滚动事件：滚动不触发 OnRender（视觉树只是平移），
        // 必须手动重绘，否则新滚入视口的片段不画（缩略图也来不及按需加载）
        Loaded += (_, _) => HookScrollHost();
    }

    private System.Windows.Controls.ScrollViewer? _scrollHost;

    private void HookScrollHost()
    {
        if (_scrollHost is not null) return;
        _scrollHost = FindScrollHost();
        if (_scrollHost is not null)
            _scrollHost.ScrollChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>可视区在内容坐标里的范围（本控件在 ScrollViewer 里，ActualWidth 是内容总宽）。</summary>
    private Rect GetViewportBounds()
    {
        var sv = _scrollHost ?? FindScrollHost();
        var left = sv?.HorizontalOffset ?? 0;
        var width = sv?.ViewportWidth ?? 0;
        if (width < 1) width = ActualWidth;
        return new Rect(left, 0, width, ActualHeight);
    }

    public ProjectFile? Project { get; set; }

    /// <summary>主选中片段（多选时为最近点中的那个）。</summary>
    public Clip? Selected { get; private set; }

    /// <summary>多选的片段集合（至少包含 <see cref="Selected"/>）。</summary>
    public HashSet<Clip> SelectedClips { get; } = new();

    public Func<Clip, ImageSource?>? ThumbnailProvider { get; set; }

    private double _playhead;
    public double Playhead
    {
        get => _playhead;
        set
        {
            if (Math.Abs(_playhead - value) < 0.0005) return;
            _playhead = value;
            InvalidateVisual();
        }
    }

    private double _pps = 56;
    public double PixelsPerSecond
    {
        get => _pps;
        set
        {
            var v = Math.Clamp(value, 0.6, 800);
            if (Math.Abs(v - _pps) < 0.001) return;
            _pps = v;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>片段主选中变化（参数为最新的主选中片段）。</summary>
    public event EventHandler<Clip?>? SelectionChanged;

    /// <summary>多选集合变化（Ctrl 点击后需要刷新属性面板时用）。</summary>
    public event EventHandler? SelectionSetChanged;

    public event EventHandler<double>? SeekRequested;

    /// <summary>素材库拖放到时间轴：参数为 (素材列表, 落点时间)。</summary>
    public event EventHandler<(System.Collections.Generic.IReadOnlyList<MediaAsset> Assets, double Time)>? AssetsDropped;

    /// <summary>片段顺序即将被拖动改变（改动前触发，主窗口据此记录撤销点）。</summary>
    public event EventHandler? ClipsReordering;

    /// <summary>片段顺序被拖动改变（先改 Project.Clips 再触发，主窗口据此刷新）。</summary>
    public event EventHandler? ClipsReordered;

    /// <summary>工程或片段变化后强制重绘。</summary>
    public void Refresh()
    {
        // 片段被删除后，选中的对象可能已不在工程里
        if (Project is not null)
        {
            SelectedClips.RemoveWhere(c => !Project.Clips.Contains(c));
            if (Selected is not null && !Project.Clips.Contains(Selected)) Selected = null;
            if (_anchorClip is not null && !Project.Clips.Contains(_anchorClip)) _anchorClip = null;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>程序化设置片段选择（删除后挑选下一个片段等）。</summary>
    public void SetClipSelection(IEnumerable<Clip> clips, Clip? primary = null)
    {
        SelectedClips.Clear();
        foreach (var c in clips) SelectedClips.Add(c);
        Selected = primary ?? SelectedClips.FirstOrDefault();
        if (Selected is not null) SelectedClips.Add(Selected);
        _anchorClip = Selected;
        InvalidateVisual();
    }

    /// <summary>
    /// 自检：模拟把指定片段拖到 targetTime 秒处（与鼠标拖动走同一套计算与提交逻辑）。
    /// 返回结果描述（OK/FAIL + 新位置）。beforeCommit 在"已拖到目标、尚未落手"时回调，
    /// 可用于截图核对插入指示线。
    /// </summary>
    public string TestReorder(Clip clip, double targetTime, Action? beforeCommit = null)
    {
        var project = Project;
        if (project is null || !project.Clips.Contains(clip)) return "FAIL clip-not-in-project";

        var origIndex = project.Clips.IndexOf(clip);
        _pressClip = clip;
        Selected = clip;
        SelectedClips.Clear();
        SelectedClips.Add(clip);

        BeginReorderDrag();
        if (_press != PressMode.DragClips) { _pressClip = null; return "FAIL begin-reorder"; }

        UpdateReorder(new Point(Math.Max(0, targetTime) * _pps, ClipTop + 10));
        var dropIndex = _dropIndex;
        beforeCommit?.Invoke();
        var dropTime = _caretX >= 0 ? Math.Max(0, _caretX / _pps) : 0;
        var changed = CommitReorder();

        // 与真实鼠标落手一致：播放头停在落点并通知预览定位
        Playhead = dropTime;
        SeekRequested?.Invoke(this, dropTime);

        var newIndex = project.Clips.IndexOf(clip);
        _dragBlock.Clear();
        _dropIndex = -1;
        _caretX = 0;
        _pressClip = null;
        _press = PressMode.None;
        StopAutoScroll();
        InvalidateVisual();

        return changed
            ? $"OK orig={origIndex} drop={dropIndex} newIndex={newIndex} count={project.Clips.Count}"
            : $"OKUNCHANGED orig={origIndex} drop={dropIndex} newIndex={newIndex} count={project.Clips.Count}";
    }

    /// <summary>自检：模拟以 from 为锚点、Shift 点击 to 的连选，返回选中数量描述。</summary>
    public string TestShiftSelect(int from, int to)
    {
        var project = Project;
        if (project is null || project.Clips.Count == 0) return "FAIL no-project";
        if (from < 0 || to < 0 || from >= project.Clips.Count || to >= project.Clips.Count)
            return $"FAIL out-of-range count={project.Clips.Count}";
        _anchorClip = project.Clips[from];
        var hit = project.Clips[to];
        var start = project.Clips.IndexOf(_anchorClip);
        var end = project.Clips.IndexOf(hit);
        SelectedClips.Clear();
        var lo = Math.Min(start, end);
        var hi = Math.Max(start, end);
        for (var i = lo; i <= hi; i++) SelectedClips.Add(project.Clips[i]);
        Selected = hit;
        InvalidateVisual();
        return $"OK from={from} to={to} count={SelectedClips.Count} expect={hi - lo + 1}";
    }

    public void ClearSelection()
    {
        SelectedClips.Clear();
        Selected = null;
        _anchorClip = null;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 键盘导航

    /// <summary>
    /// 估计视口内能看到多少个片段（按平均宽度），用作 ↑/↓ 翻页的步长。
    /// </summary>
    public int VisibleClipCount()
    {
        var project = Project;
        if (project is null || project.Clips.Count == 0) return 1;
        var per = Math.Max(1, project.TotalDuration * _pps / project.Clips.Count);
        var vp = Math.Max(1, GetViewportBounds().Width);
        return Math.Max(1, (int)(vp / per));
    }

    /// <summary>
    /// 把选中移到第 index 个片段（0 起，越界自动钳到首尾）：
    /// 更新选中、把片段滚入视口、播放头跳到片段中间帧并通知预览，
    /// 触发 SelectionChanged（属性面板与素材库随之同步）。无片段时返回 -1。
    /// <para>
    /// 播放头定位在"本片段独占区"的中间，方便直接看到字幕所在的片段中段。
    /// 独占区左界越过与上一段的转场（local &lt; 转场时长时预览显示上一段），
    /// 右界取后一片段起点 / 整条片尾，避开下一段的转场区；
    /// 中间帧被钳在这个区间内，既不在转场区，Locate 也仍判定为本片段。
    /// </para>
    /// </summary>
    public int SelectClipAt(int index)
    {
        var project = Project;
        if (project is null || project.Clips.Count == 0) return -1;
        index = Math.Clamp(index, 0, project.Clips.Count - 1);
        var clip = project.Clips[index];

        SelectedClips.Clear();
        SelectedClips.Add(clip);
        Selected = clip;
        _anchorClip = clip;
        EnsureClipOnScreen(index);
        InvalidateVisual();

        var starts = project.ComputeStarts();
        var start = starts[index];
        // 右界取后一片段起点（含转场重叠时被让出的那一段），末片段则取整条片尾。
        var end = index + 1 < project.Clips.Count ? starts[index + 1] : start + clip.Duration;
        var fps = project.Fps > 0 ? project.Fps : 25.0;
        // 独占区：左界越过与前一段的转场，右界取后一片段起点（末片段取整条片尾）。
        var inTransition = index > 0 ? clip.EffectiveTransition(project.Clips[index - 1]) : 0.0;
        var lo = Math.Min(end, start + inTransition + 1.0 / fps);
        var hi = Math.Max(lo, end - 1.0 / fps);
        // 刻度线停在本片段中间帧（方便查看字幕），并钳在独占区内避开首尾转场。
        var middle = (start + end) * 0.5;
        Playhead = Math.Clamp(Math.Clamp(middle, lo, hi), 0, project.TotalDuration);
        SeekRequested?.Invoke(this, Playhead);
        SelectionChanged?.Invoke(this, clip);
        return index;
    }

    /// <summary>把第 index 个片段滚入视口（贴边留 12px 余量）。</summary>
    private void EnsureClipOnScreen(int index)
    {
        var project = Project!;
        var starts = project.ComputeStarts();
        var x0 = starts[index] * _pps;
        var x1 = x0 + Math.Max(6, project.Clips[index].Duration * _pps);

        var sv = _scrollHost ?? FindScrollHost();
        if (sv is null) return;
        var vp = sv.ViewportWidth;
        var off = sv.HorizontalOffset;

        if (x0 < off + 12)
            sv.ScrollToHorizontalOffset(Math.Max(0, x0 - 12));
        else if (x1 > off + vp - 12)
            sv.ScrollToHorizontalOffset(Math.Min(Math.Max(0, sv.ScrollableWidth), x1 - vp + 12));
    }

    // ---------------------------------------------------------------- 布局

    protected override Size MeasureOverride(Size availableSize)
    {
        var total = Project?.TotalDuration ?? 0;
        var width = Math.Max(availableSize.Width is double.PositiveInfinity or 0 ? 0 : availableSize.Width,
            total * _pps + 60);
        return new Size(width, ClipTop + ClipHeight + 8);
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        var w = Math.Max(0, ActualWidth);
        var h = Math.Max(0, ActualHeight);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1D)), null, new Rect(0, 0, w, h));

        var project = Project;
        var total = project?.TotalDuration ?? 0;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawRuler(dc, w, total, dpi);

        if (project is null || project.Clips.Count == 0)
        {
            DrawText(dc, "把左侧素材拖到这里插入，或双击素材加入",
                new Point(16, ClipTop + 30), 12.5, Color.FromRgb(0x6B, 0x6E, 0x7A), dpi);
        }
        else
        {
            DrawClips(dc, project, dpi);
            DrawReorderOverlay(dc, project, dpi);
        }

        DrawPlayhead(dc, h);
    }

    private void DrawRuler(DrawingContext dc, double w, double total, double dpi)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x22)), null, new Rect(0, 0, w, RulerHeight));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x35)), 1), new Point(0, RulerHeight), new Point(w, RulerHeight));

        // 刻度间隔随缩放自适应
        var step = _pps >= 120 ? 0.5 : _pps >= 60 ? 1 : _pps >= 24 ? 5 : _pps >= 8 ? 15 : 60;
        var labelStep = step * (step < 1 ? 4 : step <= 1 ? 5 : 1);

        var tickPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x48)), 1);
        var maxT = Math.Max(total, w / _pps);

        for (double t = 0; t <= maxT; t += step)
        {
            var x = Math.Round(t * _pps) + 0.5;
            var major = Math.Abs(t % labelStep) < 1e-6;
            dc.DrawLine(tickPen, new Point(x, major ? 5 : 13), new Point(x, RulerHeight - 1));
            if (major)
            {
                DrawText(dc, FormatTime(t), new Point(x + 3, 3), 10, Color.FromRgb(0x8B, 0x8E, 0x9A), dpi);
            }
        }
    }

    private void DrawClips(DrawingContext dc, ProjectFile project, double dpi)
    {
        var starts = project.ComputeStarts();
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x40)), 1);
        var accentPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)), 1.6);
        var primaryPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x6B)), 2.4);
        var multiPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 232, 163, 61)), 2.0);

        // 只绘制视口附近的片段：几千个素材时逐个画文字/矩形会拖垮渲染，
        // 边距 400px 盖住转场标记和拖拽指示线的越界部分
        const double margin = 400;
        var vis = GetViewportBounds();
        var visLeft = vis.X - margin;
        var visRight = vis.Right + margin;

        for (var i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            var x = starts[i] * _pps;
            var cw = Math.Max(6, clip.Duration * _pps);
            if (x + cw < visLeft || x > visRight) continue;
            var rect = new Rect(x, ClipTop, cw - 1.5, ClipHeight);

            var body = new SolidColorBrush(clip.IsVideo ? Color.FromRgb(0x2A, 0x24, 0x1B) : Color.FromRgb(0x22, 0x25, 0x2E));
            dc.DrawRoundedRectangle(body, null, rect, 4, 4);

            // 缩略图铺满；视频用不同色调区分
            var thumb = ThumbnailProvider?.Invoke(clip);
            if (thumb is not null)
            {
                dc.PushClip(new RectangleGeometry(rect, 4, 4));
                var imageRect = CoverRect(thumb.Width, thumb.Height, rect);
                dc.DrawImage(thumb, imageRect);
                dc.Pop();
                // 底部压暗，便于看文字
                var shade = new LinearGradientBrush(
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0), new Point(0, 0.55), new Point(0, 1));
                dc.PushClip(new RectangleGeometry(rect, 4, 4));
                dc.DrawRectangle(shade, null, rect);
                dc.Pop();
            }

            var selected = SelectedClips.Contains(clip);
            var pen = ReferenceEquals(clip, Selected) ? primaryPen : selected ? multiPen : borderPen;
            dc.DrawRoundedRectangle(selected ? new SolidColorBrush(Color.FromArgb(38, 232, 163, 61)) : null, pen, rect, 4, 4);

            // 片段序号 + 名称
            DrawText(dc, $"{(i + 1):00}", new Point(x + 5, ClipTop + 4), 9.5, Color.FromArgb(210, 255, 255, 255), dpi);
            DrawText(dc, clip.FileName, new Point(x + 5, ClipTop + ClipHeight - 30), 10.2,
                Color.FromArgb(230, 255, 255, 255), dpi, Math.Max(20, cw - 12));

            var info = clip.IsVideo ? $"视频 {clip.Duration:0.0}s" : $"照片 {clip.Duration:0.0}s";
            if (clip.Motion != MotionStyle.None) info += " · 运镜";
            DrawText(dc, info, new Point(x + 5, ClipTop + ClipHeight - 16), 9.5,
                Color.FromArgb(190, 232, 163, 61), dpi, Math.Max(20, cw - 12));

            // 字幕区间条：画在片段序号下方（青蓝色，区别于转场标记的橙色）
            foreach (var cap in clip.Captions)
            {
                if (!cap.IsValid) continue;
                var s = Math.Clamp(cap.Start, 0, clip.Duration);
                var e = Math.Clamp(cap.End, 0, clip.Duration);
                if (e <= s) continue;
                var bx = x + s * _pps;
                var bw = Math.Max(3, (e - s) * _pps);
                var capRect = new Rect(bx, ClipTop + 15, Math.Min(bw, x + cw - bx), 3);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x5F, 0xB8, 0xE8)), null, capRect, 1.5, 1.5);
            }

            // 转场：在重叠区域画出标记
            if (i > 0)
            {
                var td = clip.EffectiveTransition(project.Clips[i - 1]);
                if (td > 0.01)
                {
                    var tw = Math.Max(8, td * _pps);
                    var trect = new Rect(x, ClipTop, tw, ClipHeight);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, trect);
                    var p1 = new Point(x, ClipTop);
                    var p2 = new Point(x + tw, ClipTop + ClipHeight);
                    var penT = new Pen(new SolidColorBrush(Color.FromArgb(120, 232, 163, 61)), 1);
                    dc.DrawLine(penT, p1, p2);
                    dc.DrawLine(penT, new Point(x, ClipTop + ClipHeight), new Point(x + tw, ClipTop));
                }
            }
        }
    }

    /// <summary>拖动排序的可视化：被拖块压暗 + 目标插入位置画指示线。</summary>
    private void DrawReorderOverlay(DrawingContext dc, ProjectFile project, double dpi)
    {
        if (_press != PressMode.DragClips || _dragBlock.Count == 0) return;

        var starts = project.ComputeStarts();
        var dim = new SolidColorBrush(Color.FromArgb(130, 0, 0, 0));
        foreach (var clip in _dragBlock)
        {
            var i = project.Clips.IndexOf(clip);
            if (i < 0) continue;
            var x = starts[i] * _pps;
            var cw = Math.Max(6, clip.Duration * _pps);
            dc.DrawRoundedRectangle(dim, null, new Rect(x, ClipTop, cw - 1.5, ClipHeight), 4, 4);
        }

        if (_caretX < 0) return;
        var accent = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        var cx = Math.Round(_caretX) + 0.5;
        dc.DrawLine(new Pen(accent, 2.5),
            new Point(cx, ClipTop - 4), new Point(cx, ClipTop + ClipHeight + 4));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(cx - 6, ClipTop - 12), true, true);
            ctx.LineTo(new Point(cx + 6, ClipTop - 12), true, false);
            ctx.LineTo(new Point(cx, ClipTop - 3), true, false);
        }
        dc.DrawGeometry(accent, null, head);
    }

    private void DrawPlayhead(DrawingContext dc, double h)
    {
        var x = Math.Round(_playhead * _pps) + 0.5;
        if (x < -4 || x > ActualWidth + 4) return;

        var accent = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        dc.DrawLine(new Pen(accent, 1.4), new Point(x, 0), new Point(x, h));

        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(x - 5, 0), true, true);
            ctx.LineTo(new Point(x + 5, 0), true, false);
            ctx.LineTo(new Point(x + 5, 7), true, false);
            ctx.LineTo(new Point(x, 12), true, false);
            ctx.LineTo(new Point(x - 5, 7), true, false);
        }
        dc.DrawGeometry(accent, null, head);
    }

    private static Rect CoverRect(double iw, double ih, Rect target)
    {
        if (iw <= 0 || ih <= 0) return target;
        var s = Math.Max(target.Width / iw, target.Height / ih);
        var dw = iw * s;
        var dh = ih * s;
        return new Rect(target.X + (target.Width - dw) / 2, target.Y + (target.Height - dh) / 2, dw, dh);
    }

    private void DrawText(DrawingContext dc, string text, Point origin, double size, Color color, double dpi, double maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, size,
            new SolidColorBrush(color), dpi)
        {
            Trimming = TextTrimming.CharacterEllipsis
        };
        if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
        dc.DrawText(ft, origin);
    }

    private static string FormatTime(double t)
    {
        if (t < 0) t = 0;
        var ts = TimeSpan.FromSeconds(t);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // ---------------------------------------------------------------- 拖放

    protected override void OnDragOver(DragEventArgs e)
    {
        var supported = e.Data.GetDataPresent("MemoriesStudioAssets");
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData("MemoriesStudioAssets")
            is System.Collections.Generic.IReadOnlyList<MediaAsset> assets && assets.Count > 0)
        {
            var dropTime = Math.Max(0, e.GetPosition(this).X / _pps);
            // 播放头先停在落点（追加到片尾时自动钳到片尾），随后素材在此处插入
            SeekRequested?.Invoke(this, dropTime);
            AssetsDropped?.Invoke(this, (assets, dropTime));
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _pressPos = p;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var hitClip = HitTestClip(p);
        var project = Project;

        if (hitClip is not null)
        {
            if (shift && project is not null)
            {
                // Shift 连选：以锚点为起点，选中锚点到本次点击之间的整段（含两端）。
                // 锚点不存在（首次即按住 Shift）时退化为单选并把本次点击设为锚点。
                var end = project.Clips.IndexOf(hitClip);
                var start = _anchorClip is not null ? project.Clips.IndexOf(_anchorClip) : -1;
                SelectedClips.Clear();
                if (start < 0 || end < 0)
                {
                    SelectedClips.Add(hitClip);
                }
                else
                {
                    var lo = Math.Min(start, end);
                    var hi = Math.Max(start, end);
                    for (var i = lo; i <= hi; i++) SelectedClips.Add(project.Clips[i]);
                }
                Selected = hitClip;
                _anchorClip ??= hitClip;
            }
            else if (ctrl)
            {
                if (!SelectedClips.Remove(hitClip)) SelectedClips.Add(hitClip);
                Selected = SelectedClips.Contains(hitClip) ? hitClip : SelectedClips.LastOrDefault();
                _anchorClip = hitClip;
            }
            else
            {
                SelectedClips.Clear();
                SelectedClips.Add(hitClip);
                Selected = hitClip;
                _anchorClip = hitClip;
            }
            SelectionChanged?.Invoke(this, Selected);
            SelectionSetChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!ctrl && !shift)
        {
            ClearSelection();
            SelectionChanged?.Invoke(this, null);
            SelectionSetChanged?.Invoke(this, EventArgs.Empty);
        }

        // 按到片段：先按住不动，位移超过阈值进入排序拖动；按到空白/标尺：拖动=搓播放头
        _pressClip = hitClip;
        _press = hitClip is null ? PressMode.Scrub : PressMode.MaybeDragClip;
        _dragBlock.Clear();
        _dropIndex = -1;
        _caretX = 0;
        CaptureMouse();

        SeekRequested?.Invoke(this, Math.Max(0, p.X / _pps));
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_press != PressMode.None && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_press == PressMode.MaybeDragClip)
            {
                var dx = Math.Abs(p.X - _pressPos.X);
                var dy = Math.Abs(p.Y - _pressPos.Y);
                if (dx > DragThreshold || dy > DragThreshold) BeginReorderDrag();
            }

            if (_press == PressMode.DragClips) UpdateReorder(p);
            else if (_press == PressMode.Scrub) SeekRequested?.Invoke(this, Math.Max(0, p.X / _pps));
            return;
        }

        var clipHit = HitTestClip(p);
        if (!ReferenceEquals(clipHit, _hover))
        {
            _hover = clipHit;
            Cursor = clipHit is null ? Cursors.Arrow : Cursors.Hand;
        }
    }

    /// <summary>
    /// 是否正处在"按下片段准备拖动/正在拖动排序/刚落手"的状态。
    /// 主窗口据此不自动滚动视口（否则会用旧播放头时间把视口拽回，长距离拖放后看不到落点）。
    /// </summary>
    public bool IsDragging =>
        _press is PressMode.MaybeDragClip or PressMode.DragClips || DateTime.UtcNow < _dragSettleUntil;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // 记下当前视口偏移：落手过程中主窗口会因刷新用旧播放头把视口滚走，最后要还原
        var wasDrag = _press == PressMode.DragClips;
        var sv = wasDrag ? FindScrollHost() : null;
        var keepOffset = sv?.HorizontalOffset ?? 0;

        var dropTime = -1.0;
        if (wasDrag)
        {
            StopAutoScroll();
            Cursor = Cursors.Arrow;
            // 落手后播放头停在落点（顺序没变也停在该处）
            if (_caretX >= 0)
            {
                dropTime = Math.Max(0, _caretX / _pps);
                if (_dropIndex >= 0) CommitReorder();
            }
        }

        _press = PressMode.None;
        _pressClip = null;
        _dragBlock.Clear();
        _dropIndex = -1;
        _caretX = 0;
        // 落手后短暂静默：这段时间内的刷新（定位落点、预览重绘）不再拽动视口
        if (wasDrag) _dragSettleUntil = DateTime.UtcNow.AddMilliseconds(350);
        if (Cursor != Cursors.Arrow) Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
        InvalidateVisual();

        if (dropTime >= 0) SeekRequested?.Invoke(this, dropTime);

        // 保住拖动期间用户所在的视口位置（长距离拖放后不跳回、不重新居中）
        if (sv is not null)
        {
            var clamped = Math.Clamp(keepOffset, 0, Math.Max(0, sv.ScrollableWidth));
            if (Math.Abs(clamped - sv.HorizontalOffset) > 0.5) sv.ScrollToHorizontalOffset(clamped);
        }
    }

    // ---------------------------------------------------------------- 拖动排序

    /// <summary>进入排序拖动：确定要移动的块（连续多选整块移动，否则只移动主选中片段）。</summary>
    private void BeginReorderDrag()
    {
        var project = Project;
        if (project is null || _pressClip is null || !project.Clips.Contains(_pressClip))
        {
            _press = PressMode.Scrub;
            return;
        }

        var clips = project.Clips;
        if (SelectedClips.Count > 1)
        {
            var idxs = clips.Select((c, i) => (c, i))
                .Where(x => SelectedClips.Contains(x.c))
                .Select(x => x.i)
                .OrderBy(i => i)
                .ToList();

            // 选中片段连续才整块移动；不连续时只移动按住的那个
            _dragBlock = idxs.Count > 1 && idxs[^1] - idxs[0] == idxs.Count - 1
                ? idxs.Select(i => clips[i]).ToList()
                : new System.Collections.Generic.List<Clip> { _pressClip };
        }
        else
        {
            _dragBlock = new System.Collections.Generic.List<Clip> { _pressClip };
        }

        _dropIndex = clips.IndexOf(_dragBlock[0]);
        _caretX = -1;
        _press = PressMode.DragClips;

        // 光标换成"图片/视频标志 + 数量角标"
        var allVideo = _dragBlock.All(c => c.IsVideo);
        Cursor = GetDragCursor(_dragBlock.Count, allVideo);
        EnsureAutoScrollTimer();
        _autoScrollTimer!.Start();
    }

    /// <summary>
    /// 按鼠标横坐标计算插入位置并刷新指示线。
    /// 判定必须基于"屏幕上的真实位置"（拖动块仍占着原位置绘制），
    /// 否则拖动块在被落点之前时，指示线会整体前移一个片段的宽度。
    /// 规则：数一遍中点已在鼠标左侧的剩余片段 → 该数量即插入下标。
    /// </summary>
    private void UpdateReorder(Point p)
    {
        var project = Project;
        if (project is null || _dragBlock.Count == 0) return;

        var clips = project.Clips;
        var starts = project.ComputeStarts();
        var remaining = clips.Where(c => !_dragBlock.Contains(c)).ToList();
        var t = Math.Max(0, p.X / _pps);

        var idx = 0;
        foreach (var c in remaining)
        {
            var i = clips.IndexOf(c);
            if (i < 0) continue;
            if (starts[i] + c.Duration * 0.5 < t) idx++;
        }

        _dropIndex = idx;
        _caretX = remaining.Count == 0
            ? 0
            : idx < remaining.Count
                ? starts[clips.IndexOf(remaining[idx])] * _pps
                : (starts[clips.IndexOf(remaining[^1])] + remaining[^1].Duration) * _pps;
        InvalidateVisual();
    }

    /// <summary>落手：把拖动块从原位置摘出，插到目标下标。返回顺序是否真的改变。</summary>
    private bool CommitReorder()
    {
        var project = Project;
        if (project is null || _dragBlock.Count == 0 || _dropIndex < 0) return false;

        var clips = project.Clips;
        var origIndex = clips.IndexOf(_dragBlock[0]);
        if (_dropIndex == origIndex) return false;   // 没挪动

        ClipsReordering?.Invoke(this, EventArgs.Empty);
        foreach (var c in _dragBlock) clips.Remove(c);
        for (var i = 0; i < _dragBlock.Count; i++)
            clips.Insert(Math.Min(_dropIndex + i, clips.Count), _dragBlock[i]);

        ClipsReordered?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ---------------------------------------------------------------- 拖动辅助

    /// <summary>拖到时间轴左右边缘时自动滚动（越靠边滚得越快），滚动后同步刷新插入指示线。</summary>
    private void EnsureAutoScrollTimer()
    {
        if (_autoScrollTimer is not null) return;
        _autoScrollTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(24) };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();
    }

    private void AutoScrollTick()
    {
        if (_press != PressMode.DragClips || _dragBlock.Count == 0) { StopAutoScroll(); return; }
        AutoScrollTickCore(Mouse.GetPosition(this));
    }

    /// <summary>
    /// 自动滚动判定与执行。注意：本控件的 ActualWidth 是"内容总宽"而非可视区宽，
    /// 可视区在内容坐标里是 [HorizontalOffset, HorizontalOffset + ViewportWidth]。
    /// </summary>
    /// <returns>本次是否真的滚动了。</returns>
    internal bool AutoScrollTickCore(Point p)
    {
        var sv = FindScrollHost();
        if (sv is null) return false;

        var left = sv.HorizontalOffset;
        var right = left + Math.Max(0, sv.ViewportWidth);
        if (right - left < 1) right = left + Math.Max(0, ActualWidth);

        const double edge = 60, maxSpeed = 20;
        double dx;
        if (p.X < left + edge) dx = -maxSpeed * Math.Min(1, (left + edge - p.X) / edge);
        else if (p.X > right - edge) dx = maxSpeed * Math.Min(1, (p.X - (right - edge)) / edge);
        else return false;

        var next = Math.Clamp(left + dx, 0, Math.Max(0, sv.ScrollableWidth));
        if (Math.Abs(next - left) < 0.05) return false;
        sv.ScrollToHorizontalOffset(next);

        // 滚动后鼠标相对内容的位置变了，插入位置要跟着刷新
        UpdateReorder(Mouse.GetPosition(this));
        return true;
    }

    /// <summary>自检：构造 DragClips 状态后在指定内容坐标执行一次自动滚动判定，返回滚动后的偏移。</summary>
    internal double TestAutoScroll(Point p, out bool scrolled)
    {
        var project = Project;
        if (project is null || project.Clips.Count == 0) { scrolled = false; return 0; }

        var sv = FindScrollHost();
        if (sv is null) { scrolled = false; return 0; }

        _press = PressMode.DragClips;
        if (_dragBlock.Count == 0) _dragBlock.Add(project.Clips[0]);
        scrolled = AutoScrollTickCore(p);
        // ScrollToHorizontalOffset 的生效要经过一次布局，先强制布局再读值
        UpdateLayout();
        _press = PressMode.None;
        _dragBlock.Clear();
        _dropIndex = -1;
        InvalidateVisual();
        return sv.HorizontalOffset;
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
    }

    private System.Windows.Controls.ScrollViewer? FindScrollHost()
    {
        DependencyObject d = VisualTreeHelper.GetParent(this);
        while (d is not null && d is not System.Windows.Controls.ScrollViewer)
            d = VisualTreeHelper.GetParent(d);
        return d as System.Windows.Controls.ScrollViewer;
    }

    // ---------------------------------------------------------------- 拖动光标

    /// <summary>生成"图片/视频标志 + 数量角标"光标（素材库拖放也复用）。</summary>
    public Cursor GetDragCursor(int count, bool allVideo)
    {
        var key = (Math.Max(1, count), allVideo);
        if (_cursorCache.TryGetValue(key, out var cached)) return cached;

        const int size = 48;
        var stride = size * 4;
        var bgra = BuildDragCursorBgra(count, allVideo);   // 顶行在前、直 alpha

        // .cur 像素要求自底向上：翻转行序
        var flipped = new byte[bgra.Length];
        for (var y = 0; y < size; y++)
            Array.Copy(bgra, (size - 1 - y) * stride, flipped, y * stride, stride);

        // 组装 .cur：ICONDIR + ICONDIRENTRY(热点=左上角) + BITMAPINFOHEADER + 像素 + AND 掩码
        var maskRowBytes = ((size + 31) / 32) * 4;
        var maskSize = maskRowBytes * size;
        var imgSize = 40 + flipped.Length + maskSize;
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            bw.Write((short)0); bw.Write((short)2); bw.Write((short)1);   // CUR, 1 张
            bw.Write((byte)size); bw.Write((byte)size); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)3); bw.Write((short)3);                        // 热点 (3,3)
            bw.Write(imgSize); bw.Write(22);
            bw.Write(40); bw.Write(size); bw.Write(size * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(flipped.Length + maskSize);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(flipped);
            bw.Write(new byte[maskSize]);
        }
        ms.Position = 0;
        var cursor = new Cursor(ms);
        _cursorCache[key] = cursor;
        return cursor;
    }

    /// <summary>
    /// 渲染拖动光标位图：图片（或视频）标志 + 数量角标。
    /// 返回 48×48、顶行在前、直 alpha BGRA 数据，光标与自检导出共用。
    /// </summary>
    internal static byte[] BuildDragCursorBgra(int count, bool allVideo)
    {
        const int size = 48;
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            // 图标主体：圆角卡片
            var card = new Rect(1.5, 1.5, 33, 33);
            ctx.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(225, 0x24, 0x26, 0x2F)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)), 2),
                card, 6, 6);

            var white = new SolidColorBrush(Color.FromRgb(0xEA, 0xEB, 0xF0));
            if (allVideo)
            {
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(14, 12), true, true);
                    g.LineTo(new Point(14, 26), true, false);
                    g.LineTo(new Point(26, 19), true, false);
                }
                ctx.DrawGeometry(white, null, tri);
            }
            else
            {
                // 照片图形：太阳 + 山
                ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)), null,
                    new EllipseGeometry(new Point(12, 12), 3.2, 3.2));
                var mtn = new StreamGeometry();
                using (var g = mtn.Open())
                {
                    g.BeginFigure(new Point(7, 29), true, true);
                    g.LineTo(new Point(15, 17), true, false);
                    g.LineTo(new Point(20, 24), true, false);
                    g.LineTo(new Point(23, 20), true, false);
                    g.LineTo(new Point(29, 29), true, false);
                }
                ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x9F, 0xA6, 0xB8)), null, mtn);
            }

            // 数量角标
            ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x21)), 2),
                new EllipseGeometry(new Point(38, 38), 10, 10));
            var ft = new FormattedText(count.ToString(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12.5,
                new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x21)), 1.0);
            ctx.DrawText(ft, new Point(38 - ft.Width / 2, 38 - ft.Height / 2));
        }

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[size * size * 4];
        rtb.CopyPixels(px, size * 4, 0);

        // WPF 渲染是预乘 alpha，光标要直 alpha：去预乘
        for (var o = 0; o < px.Length; o += 4)
        {
            var a = px[o + 3];
            if (a > 0 && a < 255)
            {
                px[o] = (byte)Math.Min(255, px[o] * 255 / a);
                px[o + 1] = (byte)Math.Min(255, px[o + 1] * 255 / a);
                px[o + 2] = (byte)Math.Min(255, px[o + 2] * 255 / a);
            }
        }
        return px;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        var mouseTime = e.GetPosition(this).X / _pps;
        PixelsPerSecond = _pps * (e.Delta > 0 ? 1.2 : 1 / 1.2);
        InvalidateMeasure();
        InvalidateVisual();
        _ = mouseTime;
    }

    private Clip? HitTestClip(Point p)
    {
        var project = Project;
        if (project is null || project.Clips.Count == 0) return null;
        if (p.Y < ClipTop || p.Y > ClipTop + ClipHeight) return null;

        var starts = project.ComputeStarts();
        for (var i = 0; i < project.Clips.Count; i++)
        {
            var x = starts[i] * _pps;
            var cw = Math.Max(6, project.Clips[i].Duration * _pps);
            if (p.X >= x && p.X <= x + cw) return project.Clips[i];
        }
        return null;
    }
}
