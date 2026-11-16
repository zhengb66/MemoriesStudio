using MemoriesStudio.Models;

namespace MemoriesStudio.Services;

/// <summary>画布"填充裁切"（cover）相关的换算。</summary>
public static class CoverMath
{
    /// <summary>返回源图按 cover 铺满 W×H 画布后的显示尺寸与裁切偏移（像素）。</summary>
    public static (double OffX, double OffY, double Bw, double Bh) Cover(int iw, int ih, double w, double h)
    {
        if (iw <= 0 || ih <= 0) return (0, 0, w, h);
        var s = Math.Max(w / iw, h / ih);
        var bw = iw * s;
        var bh = ih * s;
        return ((bw - w) / 2.0, (bh - h) / 2.0, bw, bh);
    }

    /// <summary>cover 铺满的缩放系数。</summary>
    public static double CoverScale(int iw, int ih, double w, double h)
        => (iw <= 0 || ih <= 0) ? 1.0 : Math.Max(w / iw, h / ih);

    /// <summary>把原图上的归一化脸高换算成画布上的归一化脸高。</summary>
    public static double FaceHeightToCanvas(double faceHeightSrc, int iw, int ih, double w, double h)
        => faceHeightSrc * ih * CoverScale(iw, ih, w, h) / h;

    /// <summary>把源图上的归一化点换算到画布坐标系（归一化）。</summary>
    public static (double X, double Y) ToCanvas(double fx, double fy, int iw, int ih, double w, double h)
    {
        var c = Cover(iw, ih, w, h);
        var px = fx * c.Bw - c.OffX;
        var py = fy * c.Bh - c.OffY;
        return (Math.Clamp(px / w, 0, 1), Math.Clamp(py / h, 0, 1));
    }

    /// <summary>
    /// 原图归一化 Y → 画布归一化 Y（不钳制）：竖幅照片里人脸可能落在可见横带之外，
    /// 结果会 &lt;0 或 &gt;1，那正是"需要平移过去"的位置。
    /// </summary>
    public static double ImageYToCanvas(double iy, int iw, int ih, double w, double h)
    {
        if (iw <= 0 || ih <= 0) return iy;
        var (_, offY, _, bh) = Cover(iw, ih, w, h);
        return (iy * bh - offY) / h;
    }

    /// <summary>
    /// 竖幅判定：原图本身是竖版/方版（高≥宽）且按 cover 铺满后纵向余量超过画布高度的 8%。
    /// 横向素材（4:3 等）纵向余量小，保留原来的缩放运镜效果更好。
    /// </summary>
    public static bool NeedsVerticalPan(int iw, int ih, double w, double h, double ratio = 1.08)
    {
        if (iw <= 0 || ih <= 0 || ih < iw * 0.95) return false;
        var (_, _, _, bh) = Cover(iw, ih, w, h);
        return bh > h * ratio;
    }

    /// <summary>
    /// 纵向平移的焦点范围（画布归一化 Y）：窗口贴住原图顶部 / 底部时的焦点值。
    /// 竖幅照片贴顶时该值常为负（画布坐标只覆盖中间一条横带，顶部在画布之外）。
    /// </summary>
    public static (double Top, double Bottom) PanYRange(int iw, int ih, double w, double h)
    {
        var (_, offY, _, _) = Cover(iw, ih, w, h);
        return (0.5 - offY / h, 0.5 + offY / h);
    }

    /// <summary>
    /// 视窗滑动位置（画布像素）：返回显示尺寸与视窗左上角偏移，预览与导出的统一取景几何。
    /// 图片按 cover 尺寸铺开（Bw×Bh），再乘缩放 z；视窗为 w×h，焦点对准其中心。
    /// </summary>
    public static (double DispW, double DispH, double Tx, double Ty) Window(
        int iw, int ih, double w, double h, double zoom, double focusX, double focusY)
    {
        if (iw <= 0 || ih <= 0) return (w, h, 0, 0);
        var (offX, offY, bw, bh) = Cover(iw, ih, w, h);
        var z = Math.Max(1.0, zoom);
        var dispW = bw * z;
        var dispH = bh * z;
        var tx = Math.Clamp((focusX * w + offX) * z - w / 2, 0, Math.Max(0, dispW - w));
        var ty = Math.Clamp((focusY * h + offY) * z - h / 2, 0, Math.Max(0, dispH - h));
        return (dispW, dispH, tx, ty);
    }
}

public static class MotionMath
{
    public readonly record struct State(double Zoom, double FocusX, double FocusY);

    // 「保留人物上半身」约束：画面窗口至少覆盖 3.6 倍头高（头顶到胸口以下），
    // 否则特写照放大后结尾只剩一个脑袋
    public const double BodyFactor = 3.6;
    // 为把人脸拉回画面中心，允许在原运镜基础上追加的最大放大倍数。
    // 只靠平移无法居中时（脸靠边，取景窗被边界挡住）才需要放大，幅度必须收敛，
    // 否则会把合影/背景整片裁掉。
    public const double CenterBoost = 1.30;
    // 头顶在画面顶部至少留出的空隙（占画面高度），避免发梢贴边看着像被裁
    public const double HeadGap = 0.02;
    // 约束允许的最大缩放上限（远景脸很小时不设限，1.6 只是保险）
    public const double MaxZoomCeiling = 1.6;
    // 旧版把焦点下移 0.9 头高（画面中心落在胸口），导致人脸被推到画面上部甚至切顶。
    // 仅用于兼容未升级的旧工程数据。
    public const double LegacyBiasFactor = 0.9;

    /// <summary>按头高允许的最大缩放：画面窗口至少覆盖 3.6 个头高。</summary>
    public static double MaxZoomFor(double faceH)
        => faceH > 0.01 ? Math.Clamp(1.0 / (faceH * BodyFactor), 1.0, MaxZoomCeiling) : MaxZoomCeiling;

    /// <summary>
    /// 根据脸高计算最大允许缩放与旧版焦点下移量；无脸高信息（FaceH=0）返回 null。
    /// BiasY 只为兼容旧工程，新片段不再下移焦点（改为人脸居中）。
    /// </summary>
    public static (double MaxZoom, double BiasY)? UpperBodyConstraint(Clip clip)
    {
        if (clip.FaceH <= 0.01) return null;
        return (MaxZoomFor(clip.FaceH), Math.Min(clip.FaceH * LegacyBiasFactor, 0.35));
    }

    /// <summary>
    /// 取景窗上边缘（画布归一化）。与导出端 zoompan 的 y 表达式、预览端 MatrixTransform
    /// 的钳制完全一致：窗口不越出画面，所以永远不会露黑边。
    /// </summary>
    public static double WindowTop(double zoom, double focusY)
    {
        var z = Math.Max(1.0, zoom);
        var max = Math.Max(0, 1 - 1 / z);
        return Math.Clamp(focusY - 0.5 / z, 0, max);
    }

    /// <summary>人脸中心在最终画面里的纵向位置（0=画面顶，1=画面底）。</summary>
    public static double FaceScreenY(double zoom, double focusY, double faceY)
        => (faceY - WindowTop(zoom, focusY)) * Math.Max(1.0, zoom);

    /// <summary>
    /// 取景求解：以人脸中心为构图中心，满足
    /// ① 头顶在画面内且留 HeadGap 空隙；② 取景窗不越界（不露黑边）；③ 缩放不超过头高允许的上限。
    /// 缩放只在"不放大就压不回中心"时提升，且最多提升 CenterBoost 倍。
    /// </summary>
    public static (double Zoom, double FocusY) FrameFace(double faceY, double faceH, double zoomBase, bool allowBoost = true)
    {
        faceY = Math.Clamp(faceY, 0, 1);
        var zBody = MaxZoomFor(faceH);
        var zBase = Math.Clamp(zoomBase, 1.0, 4.0);

        if (!allowBoost)
            return (Math.Min(zBase, zBody), faceY);

        // 搜索区间：基础缩放 → 允许的追加放大（不超过头高上限）
        var zHi = Math.Min(zBody, zBase * CenterBoost);
        if (zHi < zBase) zHi = zBody;                 // 基础缩放已超上限：按上限压缩即可

        var bestZ = Math.Min(zBase, zBody);
        var bestScore = double.PositiveInfinity;
        for (var i = 0; i <= 200; i++)
        {
            var z = Math.Round(1.0 + (zHi - 1.0) * i / 200.0, 3);
            if (z > zHi + 1e-9) break;

            var top = WindowTop(z, faceY);
            var headGap = (faceY - faceH / 2.0 - top) * z;
            if (faceH > 0.01 && headGap < HeadGap) continue;   // 这个缩放会切头顶，跳过

            var pos = (faceY - top) * z;
            // 以"脸离画面中心多远"为主，轻微惩罚偏离原运镜缩放（避免无谓放大）
            var score = Math.Abs(pos - 0.5) + Math.Abs(z - zBase) * 0.02;
            if (score < bestScore - 1e-9)
            {
                bestScore = score;
                bestZ = z;
            }
        }

        return (bestZ, faceY);
    }

    /// <summary>平滑起步/收尾，避免机械的匀速运动。</summary>
    public static double Ease(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return p * p * (3 - 2 * p);
    }

    public static State At(Clip clip, double progress)
    {
        var e = Ease(progress);
        var z = clip.StartZoom + (clip.EndZoom - clip.StartZoom) * e;
        var fx = clip.StartFocusX + (clip.EndFocusX - clip.StartFocusX) * e;
        var fy = clip.StartFocusY + (clip.EndFocusY - clip.StartFocusY) * e;

        // 竖幅纵向平移不做该约束：它靠平移取景、缩放恒为 1，焦点本就可以落在画布之外
        if (clip.PanV != 0) return new State(z, fx, fy);

        // 保留上半身：限制缩放上限
        if (UpperBodyConstraint(clip) is { } ub) z = Math.Min(z, ub.MaxZoom);

        // 兼容旧工程（MotionV<2）：旧数据把焦点下移了 0.9 头高，这里把基准搬回人脸中心，
        // 只保留原本的相对运镜偏移，避免一打开旧工程就满脸贴顶。
        if (clip.MotionV < 2 && clip.FaceH > 0.01)
        {
            var oldCenter = Math.Clamp(clip.FaceY + Math.Min(clip.FaceH * LegacyBiasFactor, 0.35), 0.05, 0.95);
            fy = Math.Clamp(clip.FaceY + (fy - oldCenter), 0, 1);
        }

        return new State(z, fx, fy);
    }
}

/// <summary>把"运镜风格"翻译成起止缩放 + 焦点轨迹（焦点均为人脸所在位置）。</summary>
public static class MotionPresets
{
    /// <summary>
    /// 人脸是否落在 cover 裁切后的画面之外（横向 4:3 照片进 16:9 时上下会被裁掉，
    /// 站在画面下缘的人物经常整个被裁出去）。这种情况必须靠纵向平移滑过去才能取到人。
    /// </summary>
    private static bool FaceOutOfView(Clip clip, double canvasW, double canvasH)
    {
        if (clip.FaceH <= 0.01) return false;
        var iw = clip.SourceWidth;
        var ih = clip.SourceHeight;
        if (iw <= 0 || ih <= 0) return false;

        var (_, _, _, bh) = CoverMath.Cover(iw, ih, canvasW, canvasH);
        if (bh <= canvasH * 1.04) return false;        // 纵向几乎没有余量，滑不动

        // FaceSY 是原图坐标（画布坐标在可见带之外会被钳成 0/1 而失真）
        var iy = clip.FaceSY > 0.01 && clip.FaceSY < 0.99 ? clip.FaceSY : clip.FaceY;
        var yCanvas = CoverMath.ImageYToCanvas(iy, iw, ih, canvasW, canvasH);
        return yCanvas < -0.02 || yCanvas > 1.02;
    }

    // 有人横向：缩放幅度（用户要求不超过 10%）
    public const double FaceZoomSpan = 0.08;
    // 无人竖幅：原图上下各不参与画面的比例
    public const double PanTrim = 0.10;
    // 无人竖幅的时长补偿（秒）——上下滑动需要更多时间才看得清
    public const double PanBonusSeconds = 1.0;

    /// <summary>
    /// 按素材自动取景（已取消手动运镜选择）：
    /// ① 有人的横向图：轻微推拉，缩放幅度 ≤10%，焦点对准人脸；
    /// ② 无人的横向图：最大化显示（整幅 cover）静止；
    /// ③ 无人的竖幅图：视窗上下滑动，原图上下各留 10% 不入画，时长 +1 秒；
    /// ④ 有人的竖幅图：视窗停在人脸所在横带，静止（以人脸为中心裁切）。
    /// </summary>
    public static void Apply(Clip clip, int index, double canvasW = 1920, double canvasH = 1080)
    {
        if (clip.Kind != MediaKind.Image) return;
        var iw = clip.SourceWidth;
        var ih = clip.SourceHeight;
        if (iw <= 0 || ih <= 0) return;

        var hasFace = clip.FaceH > 0.01;
        var portrait = ih > iw;                 // 方形按横向处理（纵向余量小，滑动看不出效果）
        var rnd = new Random(clip.Id.GetHashCode() ^ (index * 7919));
        var bonus = 0.0;

        // 横向照片里的人被 cover 裁到画面外（4:3 照片进 16:9 时下缘人物常整个被裁掉）：
        // 10% 的缩放不可能把人拉回来，只能让视窗滑过去 —— 否则这一段根本看不到人。
        var faceLost = !portrait && hasFace && FaceOutOfView(clip, canvasW, canvasH);

        if (faceLost)
        {
            var range = CoverMath.PanYRange(iw, ih, canvasW, canvasH);
            var srcY = clip.FaceSY > 0.01 && clip.FaceSY < 0.99 ? clip.FaceSY : clip.FaceY;
            var y = Math.Clamp(CoverMath.ImageYToCanvas(srcY, iw, ih, canvasW, canvasH),
                range.Top, range.Bottom);
            SetVerticalPan(clip, y, y);
        }
        else if (portrait && hasFace)
        {
            // ④ 有人竖幅：视窗滑到人脸横带后静止
            var range = CoverMath.PanYRange(iw, ih, canvasW, canvasH);
            var srcY = clip.FaceSY > 0.01 && clip.FaceSY < 0.99 ? clip.FaceSY : clip.FaceY;
            var y = Math.Clamp(CoverMath.ImageYToCanvas(srcY, iw, ih, canvasW, canvasH),
                range.Top, range.Bottom);
            SetVerticalPan(clip, y, y);
        }
        else if (portrait)
        {
            // ③ 无人竖幅：上下各裁 10% 后随机方向滑动
            var range = CoverMath.PanYRange(iw, ih, canvasW, canvasH);
            var (_, _, _, bh) = CoverMath.Cover(iw, ih, canvasW, canvasH);
            var bhRatio = bh / canvasH;                   // 原图高 = 多少倍画布高
            var start = range.Top + bhRatio * PanTrim;
            var end = range.Bottom - bhRatio * PanTrim;
            if (end - start < 0.06) { start = range.Top; end = range.Bottom; }   // 图不够高，用整段行程
            if (end - start < 0.02)
            {
                var mid = (range.Top + range.Bottom) / 2;
                SetVerticalPan(clip, mid, mid);           // 几乎没有余量，静止显示
            }
            else if (rnd.Next(2) == 0) SetVerticalPan(clip, start, end);         // 从上往下
            else SetVerticalPan(clip, end, start);                               // 从下往上
            bonus = PanBonusSeconds;
        }
        else if (hasFace)
        {
            // ① 有人横向：轻微推拉，缩放幅度 ≤10%，焦点对准人脸
            var zoomIn = rnd.Next(2) == 0;
            var fx = Math.Clamp(clip.FaceX, 0.02, 0.98);
            var fy = Math.Clamp(clip.FaceY, 0.02, 0.98);
            clip.PanV = 0;
            clip.StartZoom = zoomIn ? 1.0 : 1.0 + FaceZoomSpan;
            clip.EndZoom = zoomIn ? 1.0 + FaceZoomSpan : 1.0;
            clip.StartFocusX = clip.EndFocusX = fx;
            clip.StartFocusY = clip.EndFocusY = fy;
        }
        else
        {
            // ② 无人横向：最大化显示整幅画面，静止
            clip.PanV = 0;
            clip.StartZoom = clip.EndZoom = 1.0;
            clip.StartFocusX = clip.EndFocusX = 0.5;
            clip.StartFocusY = clip.EndFocusY = 0.5;
        }

        ApplyDurationBonus(clip, bonus);
        clip.MotionV = 3;
    }

    /// <summary>竖幅取景：视窗沿原图纵向滑动（y0 == y1 即静止）。</summary>
    private static void SetVerticalPan(Clip clip, double y0, double y1)
    {
        clip.PanV = 1;
        clip.StartZoom = clip.EndZoom = 1.0;
        clip.StartFocusX = clip.EndFocusX = 0.5;
        clip.StartFocusY = y0;
        clip.EndFocusY = y1;
    }

    /// <summary>时长补偿：只补与"已加值"的差额，重复应用策略不会越加越多。</summary>
    private static void ApplyDurationBonus(Clip clip, double want)
    {
        var cur = clip.AutoDurBonus;
        if (Math.Abs(cur - want) < 1e-6) return;
        clip.Duration = Math.Max(0.3, clip.Duration + (want - cur));
        clip.AutoDurBonus = want;
    }
}
