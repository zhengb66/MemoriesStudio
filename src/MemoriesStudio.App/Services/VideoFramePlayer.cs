using System.Diagnostics;
using System.Globalization;

namespace MemoriesStudio.Services;

/// <summary>
/// 预览用的视频解码器：调用 ffmpeg 把视频按预览尺寸解码成 BGRA 原始帧，
/// 后台线程顺序读帧，渲染线程按时间取"最近可用帧"。
/// 只解码不等待，因此在 4K 素材上预览也不会卡界面。
/// </summary>
public sealed class VideoFramePlayer : IDisposable
{
    public const int PreviewWidth = 960;
    public const int PreviewHeight = 540;

    private readonly object _lock = new();
    private readonly LinkedList<(double Time, byte[] Data)> _frames = new();

    private Process? _proc;
    private Thread? _reader;
    private volatile bool _running;
    private int _frameBytes;
    private int _width;
    private int _height;
    private double _startTime;
    private double _fps = 30;
    private double _lastRequested;

    public bool IsOpen => _proc is not null && _running;
    public int FrameWidth => _width;
    public int FrameHeight => _height;
    public string? CurrentPath { get; private set; }

    /// <summary>最近一次 GetFrame 返回帧的时间戳（素材内秒），用于诊断播放节奏。</summary>
    public double LastFrameTime { get; private set; } = -1;

    public void Open(string path, double startTime, double fps)
    {
        Close();
        if (!FfmpegService.Instance.Available) return;

        _width = PreviewWidth;
        _height = PreviewHeight;
        _frameBytes = _width * _height * 4;
        _startTime = Math.Max(0, startTime);
        _fps = Math.Clamp(fps, 12, 60);
        _lastRequested = _startTime;
        CurrentPath = path;

        var psi = new ProcessStartInfo(FfmpegService.Instance.FfmpegPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-ss", _startTime.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", path,
                     "-an",
                     "-vf", $"fps={_fps.ToString("0.###", CultureInfo.InvariantCulture)},scale={_width}:{_height}:force_original_aspect_ratio=increase,crop={_width}:{_height}",
                     "-f", "rawvideo", "-pix_fmt", "bgra", "-"
                 })
            psi.ArgumentList.Add(a);

        try
        {
            _proc = new Process { StartInfo = psi };
            _proc.Start();
            _proc.BeginErrorReadLine();
            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "VideoFrameReader" };
            _reader.Start();
        }
        catch
        {
            Close();
        }
    }

    private void ReadLoop()
    {
        var stream = _proc?.StandardOutput.BaseStream;
        if (stream is null) return;
        var buffer = new byte[_frameBytes];
        var index = 0;

        while (_running)
        {
            try
            {
                var read = 0;
                while (read < _frameBytes)
                {
                    var n = stream.Read(buffer, read, _frameBytes - read);
                    if (n <= 0) { _running = false; return; }
                    read += n;
                }
            }
            catch
            {
                return;
            }

            var ts = _startTime + index / _fps;
            index++;

            lock (_lock)
            {
                // 背压：解码进度超前播放头 0.6 秒以上时暂停读取。
                // 否则解码速度远快于实时播放，缓冲一路冲到视频结尾，画面会"快进"后定格在末帧
                while (_running &&
                       _frames.Count >= 3 &&
                       _frames.Last!.Value.Time > _lastRequested + 0.6)
                {
                    Monitor.Wait(_lock, 60);
                }
                if (!_running) return;

                // 自适应丢帧：解码进度已经落后播放头太多时，直接跳过这些帧
                if (ts < _lastRequested - 0.08) continue;
                _frames.AddLast((ts, buffer.ToArray()));
                while (_frames.Count > 8) _frames.RemoveFirst();
            }
        }
    }

    /// <summary>取 <= time 的最近一帧（没有则返回上一帧）。</summary>
    public byte[]? GetFrame(double time)
    {
        lock (_lock)
        {
            _lastRequested = time;
            Monitor.PulseAll(_lock);   // 唤醒解码线程按新播放头继续推进
            byte[]? best = null;
            foreach (var f in _frames)
            {
                if (f.Time <= time + 0.035) { best = f.Data; LastFrameTime = f.Time; }
                else break;
            }
            if (best is null && _frames.Count > 0)
            {
                best = _frames.First!.Value.Data;
                LastFrameTime = _frames.First.Value.Time;
            }
            return best;
        }
    }

    public void Close()
    {
        _running = false;
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(true);
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        lock (_lock) _frames.Clear();
        CurrentPath = null;
    }

    public void Dispose() => Close();
}
