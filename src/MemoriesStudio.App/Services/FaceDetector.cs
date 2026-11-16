using System.IO;
using System.Net.Http;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace MemoriesStudio.Services;

/// <summary>
/// 本地人脸检测（离线推理）：
/// 1) YuNet（OpenCV Zoo 2023mar ONNX，~232KB）—— 主力方案，单级联 CNN + 五点关键点，
///    通过 OpenCvSharp Dnn 模块推理、官方 FaceDetectorYN 同款后处理解码，准确率远高于 Haar；
/// 2) OpenCV Haar 级联（haarcascade_frontalface_alt2）—— YuNet 模型缺失/推理失败时的后备；
/// 3) 肤色区域分割、4) 梯度显著度重心 —— 逐级兜底，保证任何图都有合理的取景中心。
/// </summary>
public sealed class FaceDetector
{
    /// <summary>X/Y 为归一化焦点；FaceHeight 为头部高度（含发型，约 1.45× 检测框）占原图高度的比例（0 = 未知）。
    /// Method/Status 为诊断信息（检测方案与状态）。</summary>
    public sealed record FocusResult(double X, double Y, bool FaceFound, int FaceCount, double FaceHeight = 0,
        string Method = "", string Status = "");

    private static readonly Lazy<string> Dir = new(() =>
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemoriesStudio", "models");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    });

    /// <summary>随安装包捆绑的模型目录（离线机器的主要来源）。</summary>
    public static string BundledModelDir => Path.Combine(AppContext.BaseDirectory, "models");

    private const string YuNetFile = "face_detection_yunet_2023mar.onnx";
    private const string CascadeFile = "haarcascade_frontalface_alt2.xml";
    private const long YuNetMinBytes = 200_000;
    private const long CascadeMinBytes = 100_000;

    /// <summary>
    /// 按「用户目录 → 程序目录」顺序找可用模型文件（找不到返回 null）。
    /// 换一台从没跑过的电脑时用户目录是空的，此时必须能直接用安装目录里的随包模型，
    /// 不能依赖先下载成功。
    /// </summary>
    public static string? FindModelFile(string file, long minBytes)
    {
        foreach (var dir in new[] { Dir.Value, BundledModelDir })
        {
            try
            {
                var p = Path.Combine(dir, file);
                if (File.Exists(p) && new FileInfo(p).Length > minBytes) return p;
            }
            catch { }
        }
        return null;
    }

    public static string? YuNetModelPath => FindModelFile(YuNetFile, YuNetMinBytes);
    public static string? CascadeModelPath => FindModelFile(CascadeFile, CascadeMinBytes);

    public static bool ModelReady => CascadeModelPath is not null;
    public static bool YuNetReady => YuNetModelPath is not null;

    /// <summary>最近一次检测实际使用的方案（诊断用）：yunet / haar / skin / saliency / none。</summary>
    public static string LastMethod { get; private set; } = "none";

    /// <summary>最近一次检测的详细状态（诊断用）。</summary>
    public static string LastDnnStatus { get; private set; } = "-";

    /// <summary>最近一次 YuNet 尝试的状态（诊断用，独立于最终回落方案）。</summary>
    public static string LastYuNetStatus { get; private set; } = "-";

    /// <summary>最近一次检测中 YuNet 是否完整推理过（决定"无人"结论的可信度）。</summary>
    public static bool LastYuNetRan { get; private set; }

    private static CascadeClassifier? _cascade;
    private static readonly object NetLock = new();
    private static bool _downloadTried;
    private static Net? _yunet;
    private static bool _yunetBroken;

    private const string CascadeUrl =
        "https://raw.githubusercontent.com/opencv/opencv/4.x/data/haarcascades/haarcascade_frontalface_alt2.xml";

    private const string YuNetUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx";
    private const string YuNetUrlMirror =
        "https://ghproxy.net/https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx";

    /// <summary>
    /// 准备模型：**先本地后网络**。
    /// 1) 把随包模型（AppContext.BaseDirectory\models）复制到用户目录，失败也不影响（查找会自动用程序目录副本）；
    /// 2) 只有确实缺模型时才走网络，且每一步独立 try —— 早期版本级联下载失败会连带跳过随包
    ///    YuNet 的复制，导致离线/被墙的电脑上人脸检测彻底失效（表现为"模型不工作"）。
    /// </summary>
    public static async Task<bool> TryEnsureModelAsync(bool allowNetwork = true)
    {
        CopyBundledModel(YuNetFile, YuNetMinBytes);
        CopyBundledModel(CascadeFile, CascadeMinBytes);

        Log($"ensure: base={AppContext.BaseDirectory} local={Dir.Value} " +
            $"yunet={YuNetModelPath ?? "missing"} cascade={CascadeModelPath ?? "missing"} network={allowNetwork}");

        if (!allowNetwork) return YuNetReady;
        if (_downloadTried) return YuNetReady;
        _downloadTried = true;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // 级联只是后备方案，缺了不影响 YuNet 主链路
        if (!ModelReady)
        {
            try
            {
                var txt = await http.GetStringAsync(CascadeUrl).ConfigureAwait(false);
                // 合法的级联 XML（>100KB 且以 <?xml 开头）才落盘，防止存下错误页
                if (txt.Length > CascadeMinBytes && txt.TrimStart().StartsWith("<?xml"))
                    await File.WriteAllTextAsync(Path.Combine(Dir.Value, CascadeFile), txt).ConfigureAwait(false);
            }
            catch (Exception ex) { Log("cascade 下载失败（可忽略）: " + ex.Message); }
        }

        if (!YuNetReady)
        {
            byte[]? bin = null;
            try { bin = await http.GetByteArrayAsync(YuNetUrl).ConfigureAwait(false); }
            catch (Exception ex) { Log("yunet 下载失败: " + ex.Message); }

            if (bin is null || bin.Length < YuNetMinBytes)
            {
                try { bin = await http.GetByteArrayAsync(YuNetUrlMirror).ConfigureAwait(false); }
                catch (Exception ex) { Log("yunet 镜像下载失败: " + ex.Message); }
            }

            // ONNX 魔数校验（protobuf 首字节 0x08）
            if (bin is not null && bin.Length > YuNetMinBytes && bin[0] == 0x08)
            {
                try { await File.WriteAllBytesAsync(Path.Combine(Dir.Value, YuNetFile), bin).ConfigureAwait(false); }
                catch (Exception ex) { Log("yunet 落盘失败: " + ex.Message); }
            }
            else Log($"yunet 下载内容无效 len={bin?.Length ?? 0}");
        }

        Log($"ensure done: yunet={YuNetModelPath ?? "missing"} cascade={CascadeModelPath ?? "missing"}");
        return YuNetReady;
    }

    /// <summary>把随包模型复制到用户目录（已有可用文件时跳过）。失败只记日志。</summary>
    private static void CopyBundledModel(string file, long minBytes)
    {
        try
        {
            var src = Path.Combine(BundledModelDir, file);
            if (!File.Exists(src) || new FileInfo(src).Length <= minBytes) return;

            var dst = Path.Combine(Dir.Value, file);
            if (File.Exists(dst) && new FileInfo(dst).Length > minBytes) return;

            File.Copy(src, dst, true);
            Log("已把随包模型复制到用户目录: " + file);
        }
        catch (Exception ex) { Log($"复制随包模型失败 {file}: {ex.Message}"); }
    }

    private static readonly object LogGate = new();

    /// <summary>人脸检测诊断日志（%TEMP%\memories_face.log）——异机排障靠它。</summary>
    public static void Log(string msg)
    {
        try
        {
            lock (LogGate)
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "memories_face.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public FocusResult Detect(string imagePath)
    {
        FocusResult WithDiag(FocusResult r, string method)
            => r with { Method = method, Status = LastDnnStatus };

        try
        {
            // 损坏图（头部可读、解码 0 帧）在导入体检时已入缓存，这里通常只查缓存；
            // 未命中时现场解码一次——与其默默返回"无人脸"，不如明确标注文件损坏
            if (ImageHealth.IsBad(imagePath))
            {
                Log($"图片损坏，跳过检测 {imagePath}");
                return new FocusResult(0.5, 0.45, false, 0,
                    Method: "broken", Status: "文件损坏，无法解码（导出将用灰帧替代）");
            }

            using var src = LoadAsMat(imagePath);
            if (src is null || src.Empty()) return new FocusResult(0.5, 0.45, false, 0, Method: "none");

            using var work = Downscale(src, 1024);

            var byYuNet = DetectWithYuNet(work);
            if (byYuNet is not null) { LastMethod = "yunet"; return WithDiag(byYuNet, "yunet"); }

            if (LastYuNetRan)
            {
                // YuNet 完整跑过且确认画面无人 —— 比 Haar 的结论可信得多（Haar 常把
                // 花纹/建筑误检成人脸），不再回落级联，直接用显著度重心做取景中心
                LastMethod = "yunet-none";
                var salY = DetectBySaliency(work) ?? new FocusResult(0.5, 0.45, false, 0);
                return WithDiag(salY, "yunet-none");
            }

            var byNet = DetectWithHaar(work);
            if (byNet is not null) { LastMethod = "haar"; return WithDiag(byNet, "haar"); }

            var bySkin = DetectBySkinTone(work);
            if (bySkin is not null) { LastMethod = "skin"; return WithDiag(bySkin, "skin"); }

            LastMethod = "saliency";
            var sal = DetectBySaliency(work) ?? new FocusResult(0.5, 0.45, false, 0);
            return WithDiag(sal, "saliency");
        }
        catch (Exception ex)
        {
            return new FocusResult(0.5, 0.45, false, 0, Method: "error", Status: ex.Message);
        }
    }

    // ------------------------------------------------------------ 素材读取

    private static Mat? LoadAsMat(string path)
    {
        // HEIC 先转缓存 JPG（ffmpeg），否则 OpenCV/WPF 都解不开
        var rp = MediaSource.Resolve(path);
        var mat = Cv2.ImRead(rp, ImreadModes.Color);
        if (mat is not null && !mat.Empty()) return mat;

        // 其余 OpenCV 不认识的格式：WPF 解码器（WIC）兜底
        return MediaProbe.LoadImageMat(path);
    }

    private static Mat Downscale(Mat src, int maxDim)
    {
        var max = Math.Max(src.Width, src.Height);
        if (max <= maxDim) return src.Clone();
        var s = (double)maxDim / max;
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size((int)(src.Width * s), (int)(src.Height * s)), 0, 0, InterpolationFlags.Area);
        return dst;
    }

    // ------------------------------------------------------------ YuNet（主力）

    private static readonly int[] YuNetStrides = { 8, 16, 32 };
    private static readonly string[] YuNetOutputs =
        { "cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32",
          "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32" };
    private const float YuNetScoreThreshold = 0.6f;
    private const float YuNetNmsThreshold = 0.3f;
    private const int YuNetTopK = 5000;

    private static Net? GetYuNet()
    {
        lock (NetLock)
        {
            if (_yunet is not null) return _yunet;
            if (_yunetBroken) return null;

            // 候选顺序：用户目录副本 → 安装目录随包模型。
            // 用户目录里可能残留损坏/半截的旧文件（下载中断、杀软拦截），装载失败要能退回随包模型。
            var candidates = new List<string>();
            var localFirst = YuNetModelPath;
            if (localFirst is not null) candidates.Add(localFirst);
            var bundled = Path.Combine(BundledModelDir, YuNetFile);
            try
            {
                if (File.Exists(bundled) && new FileInfo(bundled).Length > YuNetMinBytes &&
                    !candidates.Contains(bundled, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(bundled);
            }
            catch { }

            if (candidates.Count == 0)
            {
                LastDnnStatus = "yunet-model-missing";
                return null;
            }

            foreach (var modelPath in candidates)
            {
                try
                {
                    var net = CvDnn.ReadNetFromOnnx(modelPath);
                    if (net.Empty())
                    {
                        net.Dispose();
                        LastDnnStatus = "yunet-load-empty " + modelPath;
                        Log("YuNet 装载失败（模型为空）: " + modelPath);
                        continue;
                    }
                    _yunet = net;
                    Log("YuNet 装载成功: " + modelPath);
                    return _yunet;
                }
                catch (Exception ex)
                {
                    LastDnnStatus = "yunet-load-failed: " + ex.Message;
                    Log($"YuNet 装载异常（{modelPath}）: {ex.Message}");
                }
            }

            // 所有候选都失败：本轮会话不再重试（避免每张照片都慢一遍）
            _yunetBroken = true;
            return null;
        }
    }

    /// <summary>
    /// YuNet 检测。后处理与官方 FaceDetectorYNImpl::postProcess 逐行对应：
    /// 三个 stride 的 cls/obj/bbox/kps 共 12 个输出，score=sqrt(cls*obj)，anchor 中心解码框，
    /// 再做 NMS。OpenCvSharp 托管层没有封装 FaceDetectorYN，这里手工补齐。
    /// </summary>
    private static FocusResult? DetectWithYuNet(Mat img)
    {
        var net = GetYuNet();
        if (net is null) return null;

        List<Mat>? blobs = null;
        LastYuNetRan = false;
        try
        {
            // 与官方 padWithDivisor 一致：补边到 32 的倍数（右/下补 0）
            var padW = (img.Width + 31) / 32 * 32;
            var padH = (img.Height + 31) / 32 * 32;
            using var pad = new Mat();
            if (padW != img.Width || padH != img.Height)
                Cv2.CopyMakeBorder(img, pad, 0, padH - img.Height, 0, padW - img.Width,
                    BorderTypes.Constant, Scalar.All(0));
            else
                img.CopyTo(pad);

            using var blob = CvDnn.BlobFromImage(pad, 1.0, new Size(padW, padH), Scalar.All(0), true, false);
            blobs = new List<Mat>();
            lock (NetLock)
            {
                net.SetInput(blob);
                // OpenCvSharp 的多输出 Forward 包装（Forward(List<Mat>, string[])）有 bug，
                // 实测始终返回空列表，这里逐个名字取输出（每次都是完整前向，12 次约 1 秒）
                foreach (var name in YuNetOutputs)
                {
                    var m = net.Forward(name);
                    blobs.Add(m);
                }
            }
            if (blobs.Count < 12)
            {
                LastDnnStatus = $"yunet-outputs={blobs.Count}";
                LastYuNetStatus = LastDnnStatus;
                return null;
            }
            LastYuNetRan = true;

            // (x1, y1, w, h, score)
            var faces = new List<(float X, float Y, float W, float H, float S)>();
            for (var i = 0; i < YuNetStrides.Length; i++)
            {
                var stride = YuNetStrides[i];
                var cols = padW / stride;
                var rows = padH / stride;
                var cls = ReadFloats(blobs[i]);
                var obj = ReadFloats(blobs[i + 3]);
                var bxs = ReadFloats(blobs[i + 6]);
                var kps = ReadFloats(blobs[i + 9]);
                if (cls is null || obj is null || bxs is null) continue;

                for (var r = 0; r < rows; r++)
                {
                    for (var c = 0; c < cols; c++)
                    {
                        var idx = r * cols + c;
                        var clsScore = Math.Clamp(cls[idx], 0f, 1f);
                        var objScore = Math.Clamp(obj[idx], 0f, 1f);
                        var score = MathF.Sqrt(clsScore * objScore);
                        if (score < YuNetScoreThreshold) continue;

                        var cx = (c + bxs[idx * 4 + 0]) * stride;
                        var cy = (r + bxs[idx * 4 + 1]) * stride;
                        var w = MathF.Exp(bxs[idx * 4 + 2]) * stride;
                        var h = MathF.Exp(bxs[idx * 4 + 3]) * stride;
                        if (w <= 4 || h <= 4) continue;
                        faces.Add((cx - w / 2, cy - h / 2, w, h, score));
                    }
                }
                _ = kps;   // 关键点暂不参与取景
            }

            LastDnnStatus = $"yunet-hits={faces.Count}";
            LastYuNetStatus = LastDnnStatus;
            if (faces.Count == 0) return null;

            // NMS（与官方一致：scoreThreshold 复用，eta=1，topK）
            var rects = faces.Select(f => new Rect((int)f.X, (int)f.Y, (int)f.W, (int)f.H)).ToList();
            var scores = faces.Select(f => f.S).ToList();
            CvDnn.NMSBoxes(rects, scores, YuNetScoreThreshold, YuNetNmsThreshold, out var keep, 1.0f, YuNetTopK);
            var kept = (keep is { Length: > 0 }
                ? keep.Select(k => faces[k])
                : faces.OrderByDescending(f => f.S)).ToList();
            LastDnnStatus = $"yunet-hits={faces.Count} kept={kept.Count}";
            if (kept.Count == 0)
            {
                LastYuNetStatus = "nms-empty";
                return null;
            }

            // 取最大脸（主拍摄对象），框宽高比异常（<0.45 或 >2.3）的丢弃
            var best = kept.Where(f => f.W / Math.Max(1f, f.H) is > 0.45f and < 2.3f)
                .OrderByDescending(f => (float)f.W * f.H).FirstOrDefault();
            if (best.W == 0)
            {
                LastYuNetStatus = $"kept={kept.Count} all-aspect-rejected";
                return null;
            }

            var fx = Math.Clamp((best.X + best.W / 2) / img.Width, 0.02, 0.98);
            var fy = Math.Clamp((best.Y + best.H / 2) / img.Height, 0.02, 0.98);
            // YuNet 框大致覆盖眉→下巴，构图用"头部高度"需含头发与后脑，约 ×1.45
            var fh = Math.Clamp(best.H / img.Height * 1.45, 0.01, 0.98);
            LastYuNetStatus = $"ok hits={faces.Count} kept={kept.Count} box=({best.X:0},{best.Y:0},{best.W:0}x{best.H:0}) score={best.S:0.00}";
            return new FocusResult(fx, fy, true, kept.Count, fh);
        }
        catch (Exception ex)
        {
            LastDnnStatus = "yunet-error: " + ex.Message;
            LastYuNetStatus = LastDnnStatus;
            return null;
        }
        finally
        {
            if (blobs is not null)
                foreach (var b in blobs) b.Dispose();
        }
    }

    private static float[]? ReadFloats(Mat m)
    {
        try
        {
            if (m.Empty() || m.Total() == 0 || m.Type() != MatType.CV_32F) return null;
            using var flat = m.Reshape(1, (int)m.Total());
            var arr = new float[(int)m.Total()];
            flat.GetArray(out arr);
            return arr;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------ Haar 级联（后备）

    private static CascadeClassifier? GetCascade()
    {
        var modelPath = CascadeModelPath;
        if (modelPath is null) return null;
        lock (NetLock)
        {
            try
            {
                if (_cascade is not null && !_cascade.Empty()) return _cascade;
                _cascade?.Dispose();
                _cascade = new CascadeClassifier(modelPath);
                return _cascade.Empty() ? null : _cascade;
            }
            catch
            {
                _cascade = null;
                return null;
            }
        }
    }

    private static FocusResult? DetectWithHaar(Mat img)
    {
        try
        {
            var cascade = GetCascade();
            if (cascade is null)
            {
                LastDnnStatus = "cascade-unavailable";
                return null;
            }

            using var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);

            // scaleFactor/minNeighbors/minSize 取宽松档：旅拍照片人脸常常很小、略侧。
            // CascadeClassifier 非线程安全，必须串行调用（并发检测线程曾直接段错误）
            Rect[] rects;
            lock (NetLock)
            {
                rects = cascade.DetectMultiScale(gray, 1.05, 4, HaarDetectionTypes.ScaleImage,
                    new Size(24, 24), new Size(0, 0));
            }
            LastDnnStatus = $"haar-hits={rects.Length}";
            if (rects.Length == 0) return null;

            var hits = rects
                .Where(r => r.Width >= img.Width * 0.015 && r.Height >= img.Height * 0.015)
                .OrderByDescending(r => (double)r.Width * r.Height)
                .ToList();
            if (hits.Count == 0) return null;

            // ---- 聚类：同一张脸常在相邻尺度被重复检出（框互相重叠/邻近），
            // ---- 而建筑物、花纹的误检通常是孤立框。把重叠或邻近的框归为一组。
            var clusters = new List<List<Rect>>();
            foreach (var r in hits)
            {
                var merged = false;
                foreach (var c in clusters)
                {
                    var u = c[0];
                    var ix = Math.Min(r.X + r.Width, u.X + u.Width) - Math.Max(r.X, u.X);
                    var iy = Math.Min(r.Y + r.Height, u.Y + u.Height) - Math.Max(r.Y, u.Y);
                    var inter = Math.Max(0, ix) * Math.Max(0, iy);
                    var dx = r.X + r.Width / 2.0 - (u.X + u.Width / 2.0);
                    var dy = r.Y + r.Height / 2.0 - (u.Y + u.Height / 2.0);
                    var dist = Math.Sqrt(dx * dx + dy * dy);
                    if (inter > 0.15 * Math.Min((double)r.Width * r.Height, (double)u.Width * u.Height)
                        || dist < 1.3 * Math.Max(r.Width, u.Width))
                    {
                        c.Add(r);
                        merged = true;
                        break;
                    }
                }
                if (!merged) clusters.Add(new List<Rect> { r });
            }

            // 优先选多框互相印证的簇（真脸标志）；全是孤立框时用肤色占比验证，
            // 全部验证不过就放弃 Haar 结果（交回肤色/显著度兜底），避免建筑物误检带偏焦点
            var multi = clusters.Where(c => c.Count >= 2)
                .OrderByDescending(c => c.Max(r => (double)r.Width * r.Height))
                .ToList();
            List<Rect>? best = null;
            if (multi.Count > 0)
            {
                best = multi[0];
            }
            else
            {
                foreach (var c in clusters.OrderByDescending(c => c.Max(r => (double)r.Width * r.Height)))
                {
                    if (SkinRatio(img, c[0]) >= 0.18) { best = c; break; }
                }
                if (best is null)
                {
                    LastDnnStatus = $"haar-clusters={clusters.Count} all-rejected";
                    return null;
                }
            }

            var clusterHits = best!
                .Select(r => (Area: (double)r.Width * r.Height,
                    Cx: r.X + r.Width / 2.0, Cy: r.Y + r.Height / 2.0,
                    H: (double)r.Height, Score: 1.0))
                .ToList();
            LastDnnStatus = $"haar-hits={rects.Length} clusters={clusters.Count} picked={clusterHits.Count}";

            double sum = 0, cx = 0, cy = 0, fh = 0;
            foreach (var h in clusterHits.OrderByDescending(v => v.Area).Take(3))
            {
                var wgt = h.Area;
                sum += wgt;
                cx += h.Cx * wgt;
                cy += h.Cy * wgt;
                fh += h.H * wgt;
            }
            if (sum <= 0) return null;

            // Haar 检测框大致覆盖眉→下巴；构图用的"头部高度"要含头发与后脑，约 ×1.45。
            // 不修正的话 maxZoom 偏大，结尾会放大到只剩一张脸
            return new FocusResult(
                Math.Clamp(cx / sum / img.Width, 0.02, 0.98),
                Math.Clamp(cy / sum / img.Height, 0.02, 0.98),
                true, clusterHits.Count,
                Math.Clamp(fh / sum / img.Height * 1.45, 0.01, 0.98));
        }
        catch (Exception ex)
        {
            LastDnnStatus = "error: " + ex.Message;
            return null;
        }
    }

    /// <summary>框内肤色像素占比（YCrCb 范围法），用于验证孤立 Haar 框是不是真脸（塔身等红色建筑可能形似人脸但无肤色分布）。</summary>
    private static double SkinRatio(Mat img, Rect r)
    {
        try
        {
            var x = Math.Clamp(r.X, 0, img.Width - 1);
            var y = Math.Clamp(r.Y, 0, img.Height - 1);
            var w = Math.Min(r.Width, img.Width - x);
            var h = Math.Min(r.Height, img.Height - y);
            if (w <= 0 || h <= 0) return 0;
            using var roi = new Mat(img, new Rect(x, y, w, h));
            using var ycrcb = new Mat();
            Cv2.CvtColor(roi, ycrcb, ColorConversionCodes.BGR2YCrCb);
            using var mask = new Mat();
            Cv2.InRange(ycrcb, new Scalar(0, 133, 77), new Scalar(255, 180, 135), mask);
            return Cv2.CountNonZero(mask) / (double)(w * h);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>诊断：跑一遍 Haar 检测，返回原始结果。</summary>
    public static string DebugDump(string imagePath)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            using var src = LoadAsMat(imagePath) ?? throw new InvalidOperationException("imread failed");
            using var work = Downscale(src, 1280);
            sb.AppendLine($"work={work.Width}x{work.Height} yunetReady={YuNetReady} cascadeReady={ModelReady}");
            sb.AppendLine($"bundled={BundledModelDir} exists={Directory.Exists(BundledModelDir)}");
            sb.AppendLine($"yunetModel={YuNetModelPath ?? "missing"} cascadeModel={CascadeModelPath ?? "missing"}");

            var final = new FaceDetector().Detect(imagePath);
            sb.AppendLine($"Detect: X={final.X:0.000} Y={final.Y:0.000} faceH={final.FaceHeight:0.000} found={final.FaceFound} n={final.FaceCount} method={LastMethod} status={LastDnnStatus}");
            sb.AppendLine($"YuNet: {LastYuNetStatus}");

            var cascade = GetCascade();
            if (cascade is null) return sb.AppendLine("cascade-null").ToString();
            using var gray = new Mat();
            Cv2.CvtColor(work, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);
            var rects = cascade.DetectMultiScale(gray, 1.05, 4, HaarDetectionTypes.ScaleImage,
                new Size(24, 24), new Size(0, 0));
            sb.AppendLine($"hits={rects.Length}");
            foreach (var r in rects.Take(6))
                sb.AppendLine($"  ({r.X},{r.Y}) {r.Width}x{r.Height}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("error: " + ex.Message);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ 退化方案

    private static FocusResult? DetectBySkinTone(Mat img)
    {
        try
        {
            using var ycrcb = new Mat();
            Cv2.CvtColor(img, ycrcb, ColorConversionCodes.BGR2YCrCb);
            using var mask = new Mat();
            Cv2.InRange(ycrcb, new Scalar(0, 133, 77), new Scalar(255, 180, 135), mask);

            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
            Cv2.GaussianBlur(mask, mask, new Size(9, 9), 0);

            Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) return null;

            var totalArea = img.Width * (double)img.Height;
            var best = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
            var area = Cv2.ContourArea(best);
            if (area < totalArea * 0.005) return null;

            // 门槛：脸部肤色块只可能占画面的一小部分。
            // 面积过大（>8%）说明是大片手臂/脖子/背景肤色而非脸部，宁可放弃走显著度兜底，
            // 否则估算出的"头高"比整幅图还大，会把缩放约束彻底废掉
            if (area > totalArea * 0.08) return null;

            // 形状必须接近椭圆（宽高比 0.45~2.2）：细长条是手臂，横条是肩背
            var br = Cv2.BoundingRect(best);
            var aspect = br.Width / (double)Math.Max(1, br.Height);
            if (aspect < 0.45 || aspect > 2.2) return null;

            var m = Cv2.Moments(best);
            if (Math.Abs(m.M00) < 1e-6) return null;

            // 肤色区域近似为脸部：由面积估算脸高，再换算成头部高度（×1.4）
            var faceH = Math.Sqrt(area) * 1.35 * 1.4 / img.Height;

            return new FocusResult(
                Math.Clamp(m.M10 / m.M00 / img.Width, 0.02, 0.98),
                Math.Clamp(m.M01 / m.M00 / img.Height, 0.02, 0.98),
                true, 1, Math.Clamp(faceH, 0.01, 0.5));
        }
        catch
        {
            return null;
        }
    }

    private static FocusResult? DetectBySaliency(Mat img)
    {
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            using var small = new Mat();
            Cv2.Resize(gray, small, new Size(64, 36), 0, 0, InterpolationFlags.Area);
            using var gx = new Mat();
            using var gy = new Mat();
            Cv2.Sobel(small, gx, MatType.CV_32F, 1, 0, 3);
            Cv2.Sobel(small, gy, MatType.CV_32F, 0, 1, 3);

            double sum = 0, cx = 0, cy = 0;
            for (var y = 0; y < small.Rows; y++)
            {
                for (var x = 0; x < small.Cols; x++)
                {
                    var g = Math.Abs(gx.At<float>(y, x)) + Math.Abs(gy.At<float>(y, x));
                    var dx = (x / (double)(small.Cols - 1)) - 0.5;
                    var dy = (y / (double)(small.Rows - 1)) - 0.45;
                    var w = g * Math.Exp(-(dx * dx + dy * dy) * 6);   // 中心加权
                    sum += w;
                    cx += w * (x + 0.5) / small.Cols;
                    cy += w * (y + 0.5) / small.Rows;
                }
            }
            if (sum < 1e-6) return null;
            return new FocusResult(Math.Clamp(cx / sum, 0.02, 0.98), Math.Clamp(cy / sum, 0.02, 0.98), false, 0);
        }
        catch
        {
            return null;
        }
    }
}
