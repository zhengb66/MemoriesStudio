using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MemoriesStudio.Controls;

/// <summary>
/// 把"起点 / 终点 / 当前位置"三个滑块合并成一根区间条：
/// 左把手 = 起点，右把手 = 终点，中间圆点 = 播放位置（只能在起止点之间拖动）。
/// 自绘 + 自己处理鼠标，避免拼三个 Slider 带来的对齐和命中问题。
/// </summary>
public sealed class TrimRangeBar : FrameworkElement
{
    private const double PadX = 10;      // 左右留白，让把手不被裁掉
    private const double TrackH = 5;
    private const double GripW = 9;
    private const double GripH = 22;
    private const double HeadR = 6.5;
    private const double GripHit = 11;
    private const double HeadHit = 9;

    private enum DragKind { None, In, Out, Pos }

    private DragKind _drag;
    private DragKind _hover;

    // ---------------------------------------------------------------- 依赖属性

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TrimRangeBar),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InPointProperty = DependencyProperty.Register(
        nameof(InPoint), typeof(double), typeof(TrimRangeBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutPointProperty = DependencyProperty.Register(
        nameof(OutPoint), typeof(double), typeof(TrimRangeBar),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(TrimRangeBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>素材总时长（秒）。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double InPoint
    {
        get => (double)GetValue(InPointProperty);
        set => SetValue(InPointProperty, value);
    }

    public double OutPoint
    {
        get => (double)GetValue(OutPointProperty);
        set => SetValue(OutPointProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>拖动中持续回调（用户主动拖动才会触发，程序赋值不触发）。</summary>
    public event Action<double>? InPointDragged;
    public event Action<double>? OutPointDragged;
    public event Action<double>? PositionDragged;

    /// <summary>一次拖动结束（松手）。</summary>
    public event Action? DragCompleted;

    public TrimRangeBar()
    {
        Height = 30;
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        // 透明底：让整块区域都能命中鼠标
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var activeState = _drag != DragKind.None ? _drag : _hover;

        var bg = new SolidColorBrush(Color.FromRgb(0x34, 0x36, 0x3F));
        bg.Freeze();
        double cy = h / 2;

        dc.DrawRoundedRectangle(bg, null, new Rect(PadX, cy - TrackH / 2, Math.Max(0, w - PadX * 2), TrackH), TrackH / 2, TrackH / 2);

        double inX = ToX(InPoint), outX = ToX(OutPoint), posX = ToX(Position);
        var accent = AccentBrush();

        if (outX - inX > 0.5)
        {
            var band = new SolidColorBrush(Color.FromArgb(0x66, 0xE8, 0xA3, 0x3D));
            band.Freeze();
            dc.DrawRoundedRectangle(band, null, new Rect(inX, cy - TrackH / 2, outX - inX, TrackH), 2, 2);
        }

        DrawGrip(dc, inX, h, accent, activeState == DragKind.In);
        DrawGrip(dc, outX, h, accent, activeState == DragKind.Out);

        // 播放头：与主窗口进度条滑块同款（浅色圆 + 强调色描边）
        var headFill = new SolidColorBrush(Color.FromRgb(0xE7, 0xE8, 0xEC));
        headFill.Freeze();
        var headPen = new Pen(accent, 2);
        headPen.Freeze();
        double r = activeState == DragKind.Pos ? HeadR + 1 : HeadR;
        dc.DrawEllipse(headFill, headPen, new Point(posX, cy), r, r);
    }

    private static void DrawGrip(DrawingContext dc, double x, double h, Brush accent, bool active)
    {
        double cy = h / 2;
        double w = active ? GripW + 2 : GripW;
        var rect = new Rect(x - w / 2, cy - GripH / 2, w, GripH);
        dc.DrawRoundedRectangle(accent, null, rect, 3, 3);

        // 中间一道浅色缝，视觉上像可抓的把手
        var notch = new SolidColorBrush(Color.FromArgb(0xB0, 0x1B, 0x1C, 0x22));
        notch.Freeze();
        dc.DrawRoundedRectangle(notch, null, new Rect(x - 1, cy - GripH / 2 + 5, 2, GripH - 10), 1, 1);
    }

    private Brush AccentBrush()
    {
        var b = (TryFindResource("AccentBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        if (b.CanFreeze && !b.IsFrozen) b.Freeze();
        return b;
    }

    // ---------------------------------------------------------------- 坐标换算

    private double Usable => Math.Max(1, ActualWidth - PadX * 2);
    private double Span => Math.Max(0.1, Maximum);

    private double ToX(double seconds) => PadX + Math.Clamp(seconds / Span, 0, 1) * Usable;
    private double ToTime(double x) => Math.Clamp((x - PadX) / Usable, 0, 1) * Span;

    // ---------------------------------------------------------------- 鼠标

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;

        double inX = ToX(InPoint), outX = ToX(OutPoint), posX = ToX(Position);
        double dIn = Math.Abs(x - inX), dOut = Math.Abs(x - outX), dPos = Math.Abs(x - posX);

        DragKind kind = DragKind.None;
        if (dIn <= GripHit && dIn <= dOut) kind = DragKind.In;            // 起点把手优先
        else if (dOut <= GripHit) kind = DragKind.Out;                    // 终点把手
        else if (dPos <= HeadHit || (x > inX && x < outX)) kind = DragKind.Pos;

        if (kind == DragKind.None) return;

        _drag = kind;
        CaptureMouse();
        e.Handled = true;

        Apply(x);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;

        if (_drag == DragKind.None)
        {
            DragKind hover = DragKind.None;
            double inX = ToX(InPoint), outX = ToX(OutPoint), posX = ToX(Position);
            double dIn = Math.Abs(x - inX), dOut = Math.Abs(x - outX), dPos = Math.Abs(x - posX);

            if (dIn <= GripHit && dIn <= dOut) hover = DragKind.In;
            else if (dOut <= GripHit) hover = DragKind.Out;
            else if (dPos <= HeadHit || (x > inX && x < outX)) hover = DragKind.Pos;

            if (hover != _hover)
            {
                _hover = hover;
                InvalidateVisual();
            }
            Cursor = hover switch
            {
                DragKind.In or DragKind.Out => Cursors.SizeWE,
                DragKind.Pos => Cursors.Hand,
                _ => Cursors.Arrow
            };
            return;
        }

        Apply(x);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None) return;

        _drag = DragKind.None;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        InvalidateVisual();
        DragCompleted?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_drag != DragKind.None) return;
        if (_hover == DragKind.None) return;
        _hover = DragKind.None;
        InvalidateVisual();
    }

    /// <summary>把鼠标横坐标换算成对应值并写回。</summary>
    private void Apply(double x)
    {
        var t = ToTime(x);
        switch (_drag)
        {
            case DragKind.In:
                // 起点必须比终点至少早 0.2s（与窗口里的最短片段一致）
                InPoint = Math.Clamp(t, 0, Math.Max(0, OutPoint - 0.2));
                InPointDragged?.Invoke(InPoint);
                break;

            case DragKind.Out:
                OutPoint = Math.Clamp(t, InPoint + 0.2, Span);
                OutPointDragged?.Invoke(OutPoint);
                break;

            case DragKind.Pos:
                Position = Math.Clamp(t, InPoint, OutPoint);
                PositionDragged?.Invoke(Position);
                break;
        }
    }
}
