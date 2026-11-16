using System.IO;
using OpenCvSharp;

namespace MemoriesStudio.Services;

/// <summary>
/// 暂停状态下的"单帧快速抓取"（进度条拖动实时预览）。
/// 用 OpenCV VideoCapture 直接 seek 到目标帧，不依赖 ffmpeg；
/// OpenCV 打不开的格式返回 null（预览显示上一帧即可，不影响导出）。
/// 输出与 VideoFramePlayer 相同的 960×540 BGRA，可直接写入同一张 WriteableBitmap。
/// </summary>
public sealed class VideoGrabber : IDisposable
{
    public const int GrabWidth = VideoFramePlayer.PreviewWidth;   // 960
    public const int GrabHeight = VideoFramePlayer.PreviewHeight; // 540
    private const int FrameBytes = GrabWidth * GrabHeight * 4;

    private readonly object _lock = new();
    private VideoCapture? _cap;
    private string? _path;

    public Task<byte[]?> GrabAsync(string path, double time)
        => Task.Run(() => Grab(path, time));

    private byte[]? Grab(string path, double time)
    {
        if (!File.Exists(path)) return null;

        lock (_lock)
        {
            try
            {
                if (_cap is null || _path != path)
                {
                    _cap?.Dispose();
                    _cap = VideoCapture.FromFile(path);
                    if (!_cap.IsOpened())
                    {
                        _cap.Dispose();
                        _cap = null;
                        _path = null;
                        return null;
                    }
                    _path = path;
                }

                var fps = _cap.Fps;
                if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) fps = 30;
                var idx = (int)Math.Round(Math.Max(0, time) * fps);
                if (_cap.FrameCount > 0) idx = Math.Min(idx, _cap.FrameCount - 1);

                using var mat = new Mat();
                _cap.Set(VideoCaptureProperties.PosFrames, idx);
                if (!_cap.Read(mat) || mat.Empty())
                {
                    // 个别封装下直接 Set 可能失败，读一帧再定位重试
                    _cap.Set(VideoCaptureProperties.PosFrames, Math.Max(0, idx - 1));
                    if (!_cap.Read(mat) || mat.Empty()) return null;
                }
                return ToPreviewBgra(mat);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>BGR 帧 → cover 裁切成 960×540 BGRA 字节。</summary>
    private static byte[] ToPreviewBgra(Mat mat)
    {
        var scale = Math.Max((double)GrabWidth / mat.Width, (double)GrabHeight / mat.Height);
        var w = (int)Math.Ceiling(mat.Width * scale);
        var h = (int)Math.Ceiling(mat.Height * scale);
        using var resized = new Mat();
        Cv2.Resize(mat, resized, new Size(w, h), 0, 0, InterpolationFlags.Linear);
        var x = Math.Max(0, (w - GrabWidth) / 2);
        var y = Math.Max(0, (h - GrabHeight) / 2);
        using var cropped = new Mat(resized, new Rect(x, y, Math.Min(GrabWidth, w), Math.Min(GrabHeight, h)));
        using var bgra = new Mat();
        Cv2.CvtColor(cropped, bgra, ColorConversionCodes.BGR2BGRA);

        var data = new byte[FrameBytes];
        bgra.ThrowIfDisposed();
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, FrameBytes);
        return data;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _cap?.Dispose(); } catch { }
            _cap = null;
            _path = null;
        }
    }
}
