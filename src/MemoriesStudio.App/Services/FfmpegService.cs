using System.IO;

namespace MemoriesStudio.Services;

/// <summary>
/// ffmpeg.exe 定位与硬件编码器检测。
/// ffmpeg 现在只用于导出 MP4 编码；素材探测 / 缩略图 / 预览 / 剪辑
/// 分别由 MediaProbe（LibVLC + OpenCV）承担。
/// </summary>
public sealed class FfmpegService
{
    public static FfmpegService Instance { get; } = new();

    private readonly Lazy<string?> _ffmpeg;

    public string? FfmpegPath => _ffmpeg.Value;
    public bool Available => FfmpegPath is not null;

    private FfmpegService()
    {
        _ffmpeg = new Lazy<string?>(() => Locate("ffmpeg"));
    }

    private static string? Locate(string name)
    {
        var fromEnv = Environment.GetEnvironmentVariable(name.ToUpperInvariant() + "_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var local = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        if (File.Exists(local)) return local;

        var local2 = Path.Combine(AppContext.BaseDirectory, "ffmpeg", name + ".exe");
        if (File.Exists(local2)) return local2;

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略非法路径 */ }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\ffmpeg\bin\" + name + ".exe",
                     @"C:\Program Files\ffmpeg\bin\" + name + ".exe",
                     @"D:\ffmpeg\bin\" + name + ".exe",
                     @"D:\src\bat\" + name + ".exe"
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>检测可用的硬件编码器（NVENC / QSV / AMF）。仅导出前探测一次。</summary>
    public async Task<string?> DetectHardwareEncoderAsync(CancellationToken ct = default)
    {
        if (FfmpegPath is null) return null;
        var psi = new System.Diagnostics.ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-encoders");
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return null;
        var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (text.Contains("h264_nvenc")) return "h264_nvenc";
        if (text.Contains("h264_qsv")) return "h264_qsv";
        if (text.Contains("h264_amf")) return "h264_amf";
        return null;
    }
}
