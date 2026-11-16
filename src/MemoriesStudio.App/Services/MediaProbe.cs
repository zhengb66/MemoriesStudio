using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MemoriesStudio.Models;
using OpenCvSharp;

namespace MemoriesStudio.Services;

/// <summary>探测结果（与旧 FfmpegService.ProbeResult 同形，便于平滑替换）。</summary>
public sealed record ProbeResult(
    int Width, int Height, double Duration, double Fps, bool HasAudio, string? VideoCodec, int Rotation);

/// <summary>
/// 素材探测 + 缩略图。除导出编码外不再依赖 ffmpeg：
/// 视频信息用 LibVLC 解析（自带解码器、全格式），缩略图用 OpenCV 直接 seek 抽帧，
/// 图片信息/缩略图用 WPF 解码器。
/// </summary>
public static class MediaProbe
{
    private static readonly object _initLock = new();
    private static readonly Lazy<LibVLC> _libvlc = new(() =>
    {
        lock (_initLock)
        {
            Core.Initialize();
            return new LibVLC(enableDebugLogs: false);
        }
    });

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".jfif" };

    public static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path));

    // ---------------------------------------------------------------- 探测

    public static async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return await Task.Run(async () =>
            IsImage(path) ? ProbeImage(path) : await ProbeVideoAsync(path, ct)).ConfigureAwait(false);
    }

    private static ProbeResult? ProbeImage(string path)
    {
        try
        {
            // HEIC 先转缓存 JPG（ffmpeg），否则无 HEIF 扩展的机器 BitmapDecoder 直接抛异常
            var rp = MediaSource.Resolve(path);
            var bmp = BitmapDecoder.Create(
                new Uri(rp), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var f = bmp.Frames.FirstOrDefault();
            if (f is null) return null;
            return new ProbeResult(f.PixelWidth, f.PixelHeight, 0, 0, false, "image", 0);
        }
        catch
        {
            // BitmapDecoder 失败（无系统 HEIF 扩展等）：走 LoadImageMat（内部同样会 Resolve）
            try
            {
                using var m = LoadImageMat(path);
                return m is null || m.Empty()
                    ? null
                    : new ProbeResult(m.Width, m.Height, 0, 0, false, "image", 0);
            }
            catch
            {
                return null;
            }
        }
    }

    private static async Task<ProbeResult?> ProbeVideoAsync(string path, CancellationToken ct)
    {
        Media? media = null;
        try
        {
            media = new Media(_libvlc.Value, path, FromType.FromPath);

            // LibVLC 3 的解析在后台进行：同步发起 + 轮询 ParsedStatus 到 Done。
            // （不能 await Parse——实测会永久挂起；也不能只调一次 Parse 就读轨道——
            //   那时解析尚未开始，恒拿到 0 轨道。15s 超时防坏文件拖死导入。）
            media.Parse(MediaParseOptions.ParseLocal);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (media.ParsedStatus != MediaParsedStatus.Done && sw.Elapsed.TotalSeconds < 15)
                await Task.Delay(30, ct).ConfigureAwait(false);
            if (media.ParsedStatus != MediaParsedStatus.Done) return null;

            double duration = media.Duration / 1000.0;
            if (duration <= 0.05) return null;

            int w = 0, h = 0, rotation = 0;
            double fps = 0;

            foreach (var t in media.Tracks)
            {
                if (t.TrackType == LibVLCSharp.Shared.TrackType.Video)
                {
                    var vd = t.Data.Video;
                    w = (int)Math.Min(uint.MaxValue, Math.Max(vd.Width, 0));
                    h = (int)Math.Min(uint.MaxValue, Math.Max(vd.Height, 0));
                    if (w > 100000 || h > 100000) { w = 0; h = 0; }
                    if (vd.FrameRateNum > 0 && vd.FrameRateDen > 0)
                        fps = (double)vd.FrameRateNum / vd.FrameRateDen;

                    // 手机竖拍：旋转元数据需要交换宽高（与导出端 ffmpeg 自动旋转后的显示尺寸一致）
                    var o = vd.Orientation;
                    if (o is VideoOrientation.LeftBottom or VideoOrientation.RightTop) rotation = 90;
                    else if (o is VideoOrientation.LeftTop or VideoOrientation.RightBottom) rotation = 270;
                    if (rotation is 90 or 270) (w, h) = (h, w);
                }
            }

            var hasAudio = media.Tracks.Any(t => t.TrackType == LibVLCSharp.Shared.TrackType.Audio);

            // 纯音频文件（设置里的背景音乐探测）：只关心时长与有无音轨
            if (w == 0 || h == 0)
                return hasAudio && duration > 0.05
                    ? new ProbeResult(0, 0, duration, 0, true, "audio", 0)
                    : null;

            return new ProbeResult(w, h, duration, fps <= 0 ? 30 : fps, hasAudio, null, rotation);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { media?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 程序退出前释放共享的 LibVLC 实例。
    /// 不主动释放的话，进程收尾时终结器释放 libvlc 可能崩溃（exit code 非零）。
    /// </summary>
    public static void Shutdown()
    {
        lock (_initLock)
        {
            if (!_libvlc.IsValueCreated) return;
            try { _libvlc.Value.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 缩略图

    public static string ThumbnailCacheDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MemoriesStudio", "thumbs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>生成缩略图，返回本地 jpg 路径（失败返回 null）。视频用 OpenCV seek 抽帧。</summary>
    public static Task<string?> CreateThumbnailAsync(string path, double timeSeconds, int width, MediaKind kind, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA1.HashData(
                        System.Text.Encoding.UTF8.GetBytes(path + "|" + width + "|" + (kind == MediaKind.Video ? timeSeconds.ToString("0.0") : "img"))))[..16]
                    .ToLowerInvariant();
                var outPath = Path.Combine(ThumbnailCacheDir, hash + ".jpg");
                if (File.Exists(outPath)) return outPath;

                Mat? mat = null;
                if (kind == MediaKind.Video)
                {
                    using var cap = VideoCapture.FromFile(path);
                    if (!cap.IsOpened()) return null;
                    var fps = cap.Fps;
                    if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) fps = 30;
                    var idx = (int)Math.Round(Math.Max(0, timeSeconds) * fps);
                    if (cap.FrameCount > 0) idx = Math.Min(idx, cap.FrameCount - 1);
                    cap.Set(VideoCaptureProperties.PosFrames, idx);
                    var m = new Mat();
                    if (!cap.Read(m) || m.Empty())
                    {
                        cap.Set(VideoCaptureProperties.PosFrames, 0);
                        if (!cap.Read(m) || m.Empty()) { m.Dispose(); return null; }
                    }
                    mat = m;
                }
                else
                {
                    var decoded = LoadImageMat(path);
                    if (decoded is null) return null;
                    mat = decoded;
                }

                using (mat)
                {
                    var scale = Math.Min((double)width / mat.Width, 2.0);
                    var w = Math.Max(1, (int)Math.Round(mat.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(mat.Height * scale));
                    using var resized = new Mat();
                    Cv2.Resize(mat, resized, new Size(w, h), 0, 0, InterpolationFlags.Area);
                    Cv2.ImWrite(outPath, resized, new ImageEncodingParam(ImwriteFlags.JpegQuality, 88));
                    return File.Exists(outPath) ? outPath : null;
                }
            }
            catch
            {
                return null;
            }
        }, ct);

    /// <summary>图片 → Mat（OpenCV 优先，WPF 解码器兜底；HEIC 先经 MediaSource 转缓存 JPG）。</summary>
    public static Mat? LoadImageMat(string path)
    {
        var rp = MediaSource.Resolve(path);
        var mat = Cv2.ImRead(rp, ImreadModes.Color);
        if (mat is not null && !mat.Empty()) return mat;

        try
        {
            var bmp = BitmapDecoder.Create(new Uri(rp), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var f = bmp.Frames.FirstOrDefault();
            if (f is null) return null;
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(f));
            var tmp = Path.Combine(Path.GetTempPath(), "ms_" + Guid.NewGuid().ToString("N") + ".jpg");
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            try { return Cv2.ImRead(tmp, ImreadModes.Color); }
            finally { try { File.Delete(tmp); } catch { } }
        }
        catch
        {
            return null;
        }
    }
}
