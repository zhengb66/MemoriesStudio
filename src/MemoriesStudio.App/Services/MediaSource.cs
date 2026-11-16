using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MemoriesStudio.Services;

/// <summary>
/// HEIC/HEIF 支持。OpenCV 与 WPF 解码器都不能直接解码 HEIC（WPF 只在系统装了
/// HEIF 扩展时才认），而随包捆绑的 ffmpeg（7.1+）可以完整解码 HEIC 主图。
/// 策略：任何需要解码的场合，先把 HEIC 转成缓存 JPG，后续统一读缓存。
/// 缓存键 = 路径|大小|修改时间 的 SHA1，源文件变化自动失效。
/// Resolve 是同步的（转换约几百毫秒），调用方都在后台线程 / 导出线程。
/// </summary>
public static class MediaSource
{
    private static readonly HashSet<string> HeicExts = new(StringComparer.OrdinalIgnoreCase)
        { ".heic", ".heif" };

    public static bool IsHeic(string path) =>
        !string.IsNullOrEmpty(path) && HeicExts.Contains(Path.GetExtension(path));

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoriesStudio", "media_cache");

    /// <summary>
    /// 把素材路径解析成可直接解码的路径：非 HEIC 原样返回；HEIC 转缓存 JPG 后返回缓存路径。
    /// 转换失败时返回原路径（让上层各自走自己的解码兜底）。
    /// </summary>
    public static string Resolve(string path)
    {
        if (!IsHeic(path)) return path;
        try
        {
            if (!File.Exists(path)) return path;
            var fi = new FileInfo(path);
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks)))[..16]
                .ToLowerInvariant();
            var cache = Path.Combine(CacheDir, key + ".jpg");
            if (File.Exists(cache) && new FileInfo(cache).Length > 1000) return cache;

            var ff = FfmpegService.Instance.FfmpegPath;
            if (ff is null)
            {
                Log($"heic-no-ffmpeg {path}");
                return path;
            }

            Directory.CreateDirectory(CacheDir);
            // 临时文件必须以 .jpg 结尾：ffmpeg 靠扩展名推断输出容器（.tmp 会报 Invalid argument）
            var tmp = cache + ".tmp.jpg";
            var psi = new ProcessStartInfo
            {
                FileName = ff,
                // 不指定 -map：HEIC 里除了主图还有缩略图/深度图等几十条流，
                // 默认流选择器会选主 tile grid（实测 4032x3024），显式 map 反而易选错。
                Arguments = "-nostdin -hide_banner -y -i \"" + path + "\" -frames:v 1 -q:v 2 \"" + tmp + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory
            };
            using var p = Process.Start(psi);
            if (p is null) return path;
            var err = p.StandardError.ReadToEnd();
            var ok = p.WaitForExit(60_000) && p.ExitCode == 0
                     && File.Exists(tmp) && new FileInfo(tmp).Length > 1000;
            if (!ok)
            {
                Log($"heic-convert-failed exit={p.ExitCode} {path}\n  {LastErrLine(err)}");
                try { File.Delete(tmp); } catch { }
                return path;
            }
            File.Move(tmp, cache, true);
            Log($"heic->jpg {path} -> {cache} ({new FileInfo(cache).Length} bytes)");
            return cache;
        }
        catch (Exception ex)
        {
            Log("heic-resolve-error " + ex.Message);
            return path;
        }
    }

    private static string LastErrLine(string err) =>
        err.Split('\n', StringSplitOptions.RemoveEmptyEntries)
           .Select(l => l.TrimEnd('\r'))
           .LastOrDefault(l => l.Contains("Error") || l.Contains("error") || l.Contains("Invalid"))
           ?? "(no error line)";

    // ------------------------------------------------------------ 视频截帧（动图 → 照片）

    /// <summary>
    /// 在指定时间点截取一帧，存成缓存 JPG 并返回路径（失败返回 null）。
    /// 用于"小于 2 秒的视频（动图）按图片入轨"：缓存键含时间点，同一视频同一时刻只截一次。
    /// </summary>
    public static string? ExtractStill(string videoPath, double seconds)
    {
        try
        {
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return null;
            var t = Math.Max(0, Math.Round(seconds, 2));
            var fi = new FileInfo(videoPath);
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                "still|" + videoPath.ToLowerInvariant() + "|" + fi.Length + "|" +
                fi.LastWriteTimeUtc.Ticks + "|" + t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))))[..16]
                .ToLowerInvariant();
            var cache = Path.Combine(CacheDir, key + ".jpg");
            if (File.Exists(cache) && new FileInfo(cache).Length > 1000) return cache;

            var ff = FfmpegService.Instance.FfmpegPath;
            if (ff is null) { Log("still-no-ffmpeg " + videoPath); return null; }

            Directory.CreateDirectory(CacheDir);
            // 临时文件必须以 .jpg 结尾：容器格式靠扩展名推断
            var tmp = cache + ".tmp.jpg";
            var psi = new ProcessStartInfo
            {
                FileName = ff,
                // -ss 放在 -i 前（快速定位）；截帧只取 1 帧
                Arguments = "-nostdin -hide_banner -y -ss " +
                            t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                            " -i \"" + videoPath + "\" -frames:v 1 -q:v 2 \"" + tmp + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var err = p.StandardError.ReadToEnd();
            var ok = p.WaitForExit(60_000) && p.ExitCode == 0
                     && File.Exists(tmp) && new FileInfo(tmp).Length > 1000;
            if (!ok)
            {
                Log($"still-failed t={t} {videoPath}\n  {LastErrLine(err)}");
                try { File.Delete(tmp); } catch { }
                return null;
            }
            File.Move(tmp, cache, true);
            Log($"still t={t} {videoPath} -> {cache}");
            return cache;
        }
        catch (Exception ex)
        {
            Log("still-error " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 工程里的动图片段（SourcePath 指向缓存截帧）如果缓存被清理，用原始视频重新截一帧。
    /// 返回 (片段, 新路径) 列表，由调用方在 UI 线程回填（绑定属性不能在后台线程改）。
    /// </summary>
    public static List<(Models.Clip Clip, string Still)> RebuildMissingStills(IEnumerable<Models.Clip> clips)
    {
        var fixedList = new List<(Models.Clip, string)>();
        foreach (var c in clips)
        {
            if (c.Kind != Models.MediaKind.Image) continue;
            var from = c.StillFrom;
            if (string.IsNullOrEmpty(from) || !File.Exists(from)) continue;
            if (File.Exists(c.SourcePath)) continue;
            var t = c.SourceDuration > 0.2 ? c.SourceDuration / 2 : 0.5;
            var still = ExtractStill(from, t);
            if (still is not null) fixedList.Add((c, still));
        }
        return fixedList;
    }

    private static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "memories_media.log"),
            DateTime.Now.ToString("MM-dd HH:mm:ss ") + s + Environment.NewLine); }
        catch { }
    }
}
