using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MemoriesStudio.Services;

/// <summary>
/// 图片健康体检：完整解码一帧判定损坏。
/// 导入探测与人脸检测只走"头部/浅层"路径——损坏 JPEG 的宽高元数据照样可读，
/// 只有完整解码才会暴露（v0.6.7 教训：一张坏图让 64 分钟的导出在中途失败）。
/// 结果按 路径|大小|mtime 缓存在 media_cache/imgcheck.json，文件未变不重检；
/// 文件被替换/修复后 mtime 变化自动失效。
/// </summary>
public static class ImageHealth
{
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoriesStudio", "media_cache");

    private static string CachePath => Path.Combine(CacheDir, "imgcheck.json");

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> Cache
    {
        get
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(CachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? new()
                    : new();
            }
            catch { _cache = new(); }
            return _cache;
        }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            lock (Gate)
                File.WriteAllText(CachePath, JsonSerializer.Serialize(Cache));
        }
        catch { }
    }

    private static string CacheKey(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch { return path + "|?"; }
    }

    /// <summary>只查缓存不做解码：true=已缓存且损坏，false=已缓存且完好，null=没有缓存记录（打开工程用，零开销）。</summary>
    public static bool? CachedVerdict(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        lock (Gate)
        {
            return Cache.TryGetValue(CacheKey(path), out var v) ? v == "bad" : null;
        }
    }

    /// <summary>同步判定（人脸检测线程用）：缓存命中直接返回，未命中现场解码一次并缓存。</summary>
    public static bool IsBad(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        var key = CacheKey(path);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var v)) return v == "bad";
        }
        var bad = !CanDecode(MediaSource.Resolve(path));
        lock (Gate)
        {
            Cache[key] = bad ? "bad" : "ok";
        }
        Persist();
        return bad;
    }

    /// <summary>异步判定（导入探测用）。</summary>
    public static async Task<bool> IsBadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        var key = CacheKey(path);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var v)) return v == "bad";
        }
        var bad = !await CanDecodeAsync(MediaSource.Resolve(path), ct).ConfigureAwait(false);
        lock (Gate)
        {
            Cache[key] = bad ? "bad" : "ok";
        }
        Persist();
        return bad;
    }

    /// <summary>强制现场解码检查（不看缓存），结果写回缓存。分块渲染失败后的重检用。</summary>
    public static async Task<bool> CheckNowAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        var bad = !await CanDecodeAsync(MediaSource.Resolve(path), ct).ConfigureAwait(false);
        lock (Gate)
        {
            Cache[CacheKey(path)] = bad ? "bad" : "ok";
        }
        Persist();
        return bad;
    }

    /// <summary>
    /// 批量体检（导出前用）：6 路并行，进度回调 (已完成, 总数)；返回坏图路径列表。
    /// 结果与导入时共用同一份缓存。
    /// </summary>
    public static async Task<List<string>> CheckManyAsync(
        IEnumerable<string> paths, Action<int, int>? onProgress, CancellationToken ct = default)
    {
        var list = paths.Distinct().ToList();
        var missing = list.Where(p => File.Exists(p) && !CacheContainsKey(p)).ToList();
        var gate = new SemaphoreSlim(6);
        var done = 0;
        var tasks = missing.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bad = !await CanDecodeAsync(MediaSource.Resolve(p), ct).ConfigureAwait(false);
                lock (Gate) Cache[CacheKey(p)] = bad ? "bad" : "ok";
            }
            catch (OperationCanceledException) { throw; }
            catch { lock (Gate) Cache[CacheKey(p)] = "bad"; }   // 检查本身异常（权限/网络盘）按损坏处理
            finally { gate.Release(); }
            var d = Interlocked.Increment(ref done);
            onProgress?.Invoke(d, missing.Count);
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (missing.Count > 0) Persist();
        return list.Where(IsCachedBad).ToList();
    }

    private static bool CacheContainsKey(string path)
    {
        lock (Gate) return Cache.ContainsKey(CacheKey(path));
    }

    private static bool IsCachedBad(string path)
    {
        lock (Gate) return Cache.TryGetValue(CacheKey(path), out var v) && v == "bad";
    }

    /// <summary>用 ffmpeg 完整解一帧（约 0.2~0.4s/张）。仅读头部元数据发现不了损坏。</summary>
    private static bool CanDecode(string path)
    {
        try
        {
            var ff = FfmpegService.Instance.FfmpegPath;
            if (ff is null) return true;   // 没有 ffmpeg 无法判定，按正常处理
            var psi = new ProcessStartInfo(ff, "")
            {
                RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentListAdd("-v", "error", "-nostdin", "-i", path, "-frames:v", "1", "-f", "null", "-");
            using var proc = Process.Start(psi)!;
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0;
        }
        catch { return true; }
    }

    private static async Task<bool> CanDecodeAsync(string path, CancellationToken ct)
    {
        try
        {
            var ff = FfmpegService.Instance.FfmpegPath;
            if (ff is null) return true;
            var psi = new ProcessStartInfo(ff, "")
            {
                RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");
            using var proc = Process.Start(psi)!;
            _ = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { return true; }
    }

    /// <summary>生成（并缓存）与源图同尺寸的灰帧 JPG，作为损坏图片在导出时的替身。</summary>
    public static async Task<string?> GetPlaceholderAsync(int w, int h, string sourcePath, CancellationToken ct = default)
    {
        try
        {
            w = Math.Max(16, w > 0 ? w : 1920);
            h = Math.Max(16, h > 0 ? h : 1080);
            var key = "bad_" + Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{sourcePath}|{w}x{h}"))).ToLowerInvariant();
            var outPath = Path.Combine(CacheDir, key + ".jpg");
            if (File.Exists(outPath)) return outPath;

            Directory.CreateDirectory(CacheDir);
            var ff = FfmpegService.Instance.FfmpegPath;
            if (ff is null) return null;
            var psi = new ProcessStartInfo(ff, "")
            {
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add($"color=c=0x303030:s={w}x{h}");
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add(outPath);
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 && File.Exists(outPath) ? outPath : null;
        }
        catch { return null; }
    }
}

/// <summary>ProcessStartInfo.ArgumentList 的快捷追加（C# 9 不支持集合表达式初始化后的追加写法）。</summary>
internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo ArgumentListAdd(this ProcessStartInfo psi, params string[] args)
    {
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
