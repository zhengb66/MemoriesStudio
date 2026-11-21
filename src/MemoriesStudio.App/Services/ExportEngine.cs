using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MemoriesStudio.Models;

namespace MemoriesStudio.Services;

/// <summary>视频编码格式：AVC(H.264) 兼容性最好，HEVC(H.265) 同画质体积更小。</summary>
public enum VideoCodec { Avc, Hevc }

public sealed record ExportOptions(
    string OutputPath,
    int Width,
    int Height,
    int Fps,
    int Crf,
    VideoCodec Codec)
{
    /// <summary>默认规则：宽度 ≤1920（720p/1080p）用 AVC，2K/4K 用 HEVC。</summary>
    public static VideoCodec DefaultCodecFor(int width) => width <= 1920 ? VideoCodec.Avc : VideoCodec.Hevc;

    /// <summary>编码格式的界面显示名。</summary>
    public static string DisplayName(VideoCodec codec)
        => codec == VideoCodec.Hevc ? "HEVC（H.265）" : "AVC（H.264）";
}

public sealed record ExportProgress(double Percent, string Status, double Seconds);

/// <summary>ffmpeg 进程失败：携带退出码与完整 stderr，供调用方按错误类别降级处理。</summary>
public sealed class FfmpegException : InvalidOperationException
{
    public int ExitCode { get; }
    public string Stderr { get; }
    public FfmpegException(int exitCode, string stderr)
        : base("FFmpeg 导出失败（退出码 " + exitCode + "）：\n" + stderr)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    /// <summary>EOF 类失败：退出码低字节为 AVERROR_EOF('E'=0x45=69)，或 stderr 出现 EOF/截断线索。</summary>
    public bool IsEofFailure =>
        ExitCode == 69
        || Stderr.Contains("End of file", StringComparison.OrdinalIgnoreCase)
        || Stderr.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase)
        || Stderr.Contains("No more data", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 导出引擎：把时间轴编译成一条 FFmpeg filter_complex 流水线。
/// 画面走 zoompan 实现人脸为中心的运镜，片段之间用 xfade 做转场；
/// 声音走 amix —— 视频原声与背景音乐并行混合，音乐不会顶掉原声。
/// </summary>
public sealed class ExportEngine
{
    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public sealed record Plan(List<string> Args, string FilterScript, double TotalDuration, string Preview,
        List<string>? CaptionFiles = null);

    /// <summary>单块最多片段数：内存有界（每片段的滤镜队列约 15MB），且命令行/滤镜图规模可控。</summary>
    private const int ChunkMaxClips = 60;

    /// <summary>导出前素材体检发现的损坏图片（本次导出已用灰帧替代），供 UI 提示。</summary>
    public List<string> LastSwappedFiles { get; } = new();

    /// <summary>字幕 drawtext 用的字体（微软雅黑粗体优先，Windows 自带；缺失时退而求其次）。</summary>
    private static readonly string? CaptionFontPath = new[]
    {
        @"C:\Windows\Fonts\msyhbd.ttc",
        @"C:\Windows\Fonts\msyh.ttc",
        @"C:\Windows\Fonts\simhei.ttf",
        @"C:\Windows\Fonts\arial.ttf",
    }.FirstOrDefault(File.Exists);

    /// <summary>
    /// 生成一个字幕段的 drawtext 滤镜。文字写入 UTF-8 textfile 临时文件，
    /// 彻底绕开 drawtext 的转义地狱（冒号/引号/百分号/中文）；
    /// 时间用片段局部秒（片段滤镜图的 t 从 0 计），enable 控制区间、alpha 做淡入淡出。
    /// </summary>
    private string BuildDrawText(Caption cap, int H, List<string> textFiles)
    {
        var textFile = Path.Combine(Path.GetTempPath(), "ms_cap_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(textFile, cap.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        textFiles.Add(textFile);

        var fontPx = Math.Max(14, (int)Math.Round(H * cap.FontSizePct / 100.0));
        var (fill, border) = Caption.Colors(cap.ColorPreset);
        var margin = (int)Math.Round(H * Caption.EdgeMarginPct / 100.0);
        var yExpr = cap.Position == CaptionPos.Top ? $"y={margin}" : $"y=h-th-{margin}";
        var fade = cap.Fade > 0.001
            ? $":alpha='if(lt(t,{F(cap.Start + cap.Fade)}),min((t-{F(cap.Start)})/{F(cap.Fade)},1)," +
              $"if(gt(t,{F(cap.End - cap.Fade)}),min(({F(cap.End)}-t)/{F(cap.Fade)},1),1))'"
            : "";
        var borderW = Math.Max(2, fontPx / 16);
        // Windows 盘符里的冒号是 filter 参数分隔符，单引号保护不了它，必须转义成 C\:；
        // expansion=none 关闭 %{...} 文本展开——孤立 % 会让展开失败、drawtext 静默不画字
        var font = CaptionFontPath!.Replace('\\', '/').Replace(":", "\\:");
        var tf = textFile.Replace('\\', '/').Replace(":", "\\:");

        return $"drawtext=fontfile='{font}':textfile='{tf}':expansion=none" +
               $":fontsize={fontPx}:fontcolor={fill}:borderw={borderW}:bordercolor={border}" +
               $":x=(w-text_w)/2:{yExpr}:enable='between(t,{F(cap.Start)},{F(cap.End)})'{fade},";
    }

    /// <summary>某片段全部有效字幕的 drawtext 链（无字幕返回空串）。</summary>
    private string CaptionFilters(Clip c, int H, List<string> textFiles)
    {
        if (CaptionFontPath is null) return "";
        var caps = c.Captions.Where(x => x.IsValid).ToList();
        return caps.Count == 0 ? "" : string.Concat(caps.Select(cap => BuildDrawText(cap, H, textFiles)));
    }

    private readonly List<(Clip Clip, string Path)> _pendingRestore = new();

    public async Task ExportAsync(ProjectFile project, ExportOptions opt,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (!FfmpegService.Instance.Available)
            throw new InvalidOperationException("未找到 FFmpeg / FFprobe，请把 ffmpeg.exe 放到程序目录或加入 PATH。");
        if (project.Clips.Count == 0)
            throw new InvalidOperationException("时间轴里没有任何片段。");

        var outDir = Path.GetDirectoryName(opt.OutputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        int W = opt.Width - opt.Width % 2;
        int H = opt.Height - opt.Height % 2;
        PrepareClips(project, W, H);

        var clips = project.Clips.ToList();
        var n = clips.Count;

        // ---- 0. 图片体检：解码失败的损坏图片换成同尺寸灰帧 ----
        // JPEG 可能"头部可读、数据损坏"：导入探测能拿到宽高，解码器却出不了帧 →
        // 导出中该输入分支提前 EOF（退出码 69），几十分钟的渲染到中段才失败。
        // 体检按 路径|大小|mtime 缓存（重复导出零开销），坏图替换、原图路径不受影响。
        var swapped = await PreValidateImagesAsync(clips, progress, ct).ConfigureAwait(false);
        foreach (var (idx, ph) in swapped) SwapClipPath(clips[idx], ph);

        try
        {
            await ExportCoreAsync(project, clips, opt, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            // 恢复原始路径：工程对象保持干净（导出后另存不会写入替身路径）
            foreach (var (c, p) in _pendingRestore) c.SourcePath = p;
            _pendingRestore.Clear();
        }
    }

    /// <summary>临时把片段指向替身文件，登记原始路径待导出结束后还原。</summary>
    private void SwapClipPath(Clip c, string newPath)
    {
        _pendingRestore.Add((c, c.SourcePath));
        c.SourcePath = newPath;
    }

    private async Task ExportCoreAsync(ProjectFile project, List<Clip> clips, ExportOptions opt,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        int W = opt.Width - opt.Width % 2;
        int H = opt.Height - opt.Height % 2;
        var n = clips.Count;
        ComputeTiming(clips, opt.Fps, null, out _, out var dur, out var td, out var starts, out var total);
        var bounds = ComputeChunkBounds(dur, td, n, W, H);

        if (bounds.Count == 2)
        {
            // 单块：一条命令完整渲染（小工程原行为）
            var plan = BuildPlanCore(project, opt, opt.OutputPath, false, null, null);
            await RunPlanAsync(plan, opt, plan.TotalDuration, 0, plan.TotalDuration, "", progress, ct).ConfigureAwait(false);
            progress?.Report(new ExportProgress(100, "导出完成", total));
            return;
        }

        // ---- 分块渲染 ----
        // 任意时刻 ffmpeg 只在合成相邻 2 个素材的转场，但把全部输入塞进一条命令会让
        // 滤镜图把海量已生成帧堆在队列里（内存随片段数线性暴涨，命令行也会超 32K 上限）。
        // 三步走：① 按少量素材成块渲染（照片走 concat 清单，命令行不超长）；
        // ② 分块无损拼接（流复制，不重编码）；③ 终段只做音频混音。
        var chunkDir = Path.Combine(Path.GetTempPath(), "ms_chunks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(chunkDir);
        try
        {
            var chunkFiles = new List<string>();
            var doneSec = 0.0;
            for (var c = 0; c < bounds.Count - 1; c++)
            {
                ct.ThrowIfCancellationRequested();
                var from = bounds[c];
                var to = bounds[c + 1];
                var startAbs = c == 0 ? 0 : starts[from] + td[from];
                var endAbs = c < bounds.Count - 2 ? starts[to] + td[to] : total;
                var chunkLen = endAbs - startAbs;

                // 块子工程（不带配乐）：非首块的首片段裁掉进入转场段（上一块已渲染过这段），
                // 同时运镜进度平移 td[from]——否则首片段的缩放/滑动会跳回起点（接缝跳帧）
                var sub = new ProjectFile { Name = project.Name, Width = project.Width, Height = project.Height, Fps = project.Fps };
                for (var i = from; i <= to; i++) sub.Clips.Add(clips[i]);
                var firstOverride = c == 0 ? null : (double?)(dur[from] - td[from]);
                var motionOffset = c == 0 ? 0 : td[from];

                var chunkFile = Path.Combine(chunkDir, $"chunk_{c:D3}.mp4");
                var plan = BuildPlanCore(sub, opt, chunkFile, true, chunkLen, firstOverride, motionOffset);
                var expectedFrames = Math.Max(1, (int)Math.Round(chunkLen * opt.Fps));
                try
                {
                    await RunPlanAsync(plan, opt, total, doneSec, chunkLen, $"分块 {c + 1}/{bounds.Count - 1} ", progress, ct).ConfigureAwait(false);
                }
                catch (FfmpegException ex) when (ex.IsEofFailure)
                {
                    // 韧性：EOF 类失败（某素材提前截断/损坏）时输出可能已被完整写出
                    //（滤镜分支的 tpad/trim 保证帧数齐备）。数包验证达标就降级接受，
                    // 否则原样抛出。
                    var frames = File.Exists(chunkFile) ? CountVideoFrames(chunkFile) : -1;
                    var minFrames = expectedFrames - Math.Max(3, expectedFrames / 50);
                    if (frames < minFrames)
                    {
                        // 兜底：块内某张图片解码失败（体检缓存过期/文件临时读不出来）→ 灰帧替代后重渲一次
                        AppendExportLog($"== chunk {c} EOF 失败且输出不完整：frames={frames} expected={expectedFrames} ==\n{ex.Message}\n");
                        var fixedAny = false;
                        foreach (var cl in sub.Clips)
                        {
                            if (cl.Kind != MediaKind.Image) continue;
                            if (!await ImageHealth.CheckNowAsync(cl.SourcePath, ct).ConfigureAwait(false)) continue;
                            var ph = await ImageHealth.GetPlaceholderAsync(
                                cl.SourceWidth, cl.SourceHeight, cl.SourcePath, ct).ConfigureAwait(false);
                            if (ph is null) continue;
                            AppendExportLog($"== chunk {c} 素材损坏，灰帧替代：{cl.SourcePath} ==\n");
                            LastSwappedFiles.Add(cl.SourcePath);
                            SwapClipPath(cl, ph);
                            fixedAny = true;
                        }
                        if (!fixedAny) throw;
                        progress?.Report(new ExportProgress(
                            Math.Clamp(doneSec / total * 100, 0, 99.5),
                            $"分块 {c + 1}：发现损坏素材，已替换灰帧重新渲染", doneSec));
                        var retryPlan = BuildPlanCore(sub, opt, chunkFile, true, chunkLen, firstOverride, motionOffset);
                        await RunPlanAsync(retryPlan, opt, total, doneSec, chunkLen,
                            $"分块 {c + 1}/{bounds.Count - 1} ", progress, ct).ConfigureAwait(false);
                        chunkFiles.Add(chunkFile);
                        doneSec += chunkLen;
                        continue;
                    }
                    AppendExportLog($"== chunk {c} EOF 降级接受：frames={frames} expected={expectedFrames} ==\n");
                    progress?.Report(new ExportProgress(
                        Math.Clamp((doneSec + chunkLen) / total * 100, 0, 99.5),
                        $"分块 {c + 1}：素材提前结束，已用末帧补齐继续导出", doneSec + chunkLen));
                }
                chunkFiles.Add(chunkFile);
                doneSec += chunkLen;
            }

            // 终段 1+2：无损拼接（流复制）+ 音频混音
            progress?.Report(new ExportProgress(Math.Clamp(doneSec / total * 100, 0, 99.5), "正在无损拼接分块…", doneSec));
            var listPath = Path.Combine(chunkDir, "chunks.txt");
            var lines = new StringBuilder("ffconcat version 1.0\n");
            foreach (var f in chunkFiles)
                lines.Append("file '").Append(f.Replace('\\', '/').Replace("'", "'\\''")).AppendLine("'");
            // 注意：mp4 分块各自自带时长，不能像照片清单那样重复末尾文件（会重复播放末块）
            await File.WriteAllTextAsync(listPath, lines.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);

            await MixAudioAsync(project, clips, dur, starts, total, listPath, opt, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            // MEMORIES_KEEP_CHUNKS=1 时保留分块中间产物（诊断块边界帧数用）
            if (Environment.GetEnvironmentVariable("MEMORIES_KEEP_CHUNKS") != "1")
                try { Directory.Delete(chunkDir, true); } catch { }
        }

        progress?.Report(new ExportProgress(100, "导出完成", total));
    }

    /// <summary>执行一条 Plan（写临时文件 → 起 ffmpeg → 进度上报 → 失败抛异常）。</summary>
    /// <param name="overallTotal">整条时间轴总时长（百分比分母）。</param>
    /// <param name="progressBase">本命令覆盖区间在整条时间轴上的起点（秒）。</param>
    /// <param name="progressSpan">本命令覆盖区间长度（秒），进度钳制在区间内。</param>
    private static async Task RunPlanAsync(Plan plan, ExportOptions opt, double overallTotal,
        double progressBase, double progressSpan, string statusPrefix, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "ms_filter_" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(scriptPath, plan.FilterScript, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var args = new List<string>(plan.Args);
        args.InsertRange(args.Count - 1, new[] { "-filter_complex_script", scriptPath });

        var psi = new ProcessStartInfo(FfmpegService.Instance.FfmpegPath!)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 显式指定有效工作目录：若主进程 CWD 指向已被改名/删除的目录
            //（如从资源管理器双击工程启动后目录被移动），CreateProcess 会直接失败
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var errors = new StringBuilder();
        var overall = Math.Max(overallTotal, 0.001);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            errors.AppendLine(e.Data);
            if (errors.Length > 20000) errors.Remove(0, 10000);
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // -progress pipe:1 输出 out_time_us=微秒
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                var v = e.Data[12..].Trim();
                if (long.TryParse(v, out var us) && us > 0)
                {
                    var sec = us / 1_000_000.0;
                    // 百分比永远按整条时间轴算：progressBase+sec 是全局位置，
                    // 分母若用本命令区间长，第一块就会先冲到 100%
                    var g = Math.Min(progressBase + sec, progressBase + progressSpan);
                    var pct = Math.Clamp(g / overall * 100.0, 0, 99.5);
                    progress?.Report(new ExportProgress(pct, $"{statusPrefix}正在渲染 {g:0.0}s / {overall:0.0}s", g));
                }
            }
        };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            while (!proc.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(120, ct).ConfigureAwait(false);
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            if (plan.CaptionFiles is not null)
                foreach (var f in plan.CaptionFiles)
                    try { File.Delete(f); } catch { }
        }

        if (proc.ExitCode != 0)
        {
            var tail = errors.ToString();
            // 完整命令行与 stderr 落盘：GUI 失败弹窗只能看到尾部，事后排查全靠这份日志
            AppendExportLog($"== ffmpeg FAIL {DateTime.Now:HH:mm:ss} exit={proc.ExitCode} ==\n" +
                            "cmd: ffmpeg " + string.Join(" ", args) + "\nstderr:\n" + tail + "\n");
            if (tail.Length > 3000) tail = tail[^3000..];
            throw new FfmpegException(proc.ExitCode, tail);
        }
    }

    /// <summary>
    /// 把诊断信息追加到 %TEMP%\memories_export_errors.log（失败、降级、素材替换都记）。
    /// 与 memories_export.log 分开：无头模式会在结束时整文件重写 memories_export.log，
    /// 追加型诊断若写同一文件会被覆盖丢失（v0.6.6 排查时踩过）。
    /// </summary>
    private static void AppendExportLog(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "memories_export_errors.log"),
                text + "\n--------------------\n", new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>用 ffmpeg 流复制数包（≈帧数）：-c copy -f null 只解封装，速度快。</summary>
    private static int CountVideoFrames(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(FfmpegService.Instance.FfmpegPath!)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("info");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            // 最后一行 "frame= N" 即总数
            foreach (var line in err.Split('\n').Reverse())
            {
                var m = Regex.Match(line.Trim(), @"^frame=\s*(\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// 终段：分块无损拼接（视频流复制，不重编码）+ 音频混音。
    /// 视频原声重新引用原视频文件取音频流；配乐按列表混入，命令行规模都很小。
    /// </summary>
    private async Task MixAudioAsync(ProjectFile project, List<Clip> clips, double[] dur, double[] starts,
        double total, string chunkListPath, ExportOptions opt, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var fc = new StringBuilder();

        args.Add("-f"); args.Add("concat");
        args.Add("-safe"); args.Add("0");
        args.Add("-i"); args.Add(chunkListPath);

        var videoInput = new Dictionary<int, int>();
        for (var i = 0; i < clips.Count; i++)
        {
            if (clips[i].Kind != MediaKind.Video || !clips[i].HasAudio) continue;
            var c = clips[i];
            // 与画面分支同款钳制：请求越过文件末尾会让混音命令提前 EOF 报错
            var ssIn = Math.Max(0, Math.Min(c.SourceIn, c.SourceDuration - 0.04));
            var tReq = Math.Min(dur[i], Math.Max(0.04, c.SourceDuration - ssIn - 0.04));
            args.Add("-ss"); args.Add(F(ssIn));
            args.Add("-t"); args.Add(F(tReq));
            args.Add("-i"); args.Add(c.SourcePath);
            videoInput[i] = 1 + videoInput.Count;
        }

        var silenceIndex = 1 + videoInput.Count;
        args.Add("-f"); args.Add("lavfi");
        args.Add("-t"); args.Add(F(total + 0.5));
        args.Add("-i"); args.Add("anullsrc=r=48000:cl=stereo");

        var musicSlots = BuildMusicSlots(project, total);
        var audioInputs = new List<(AudioItem Item, int Index)>();
        var nextInput = silenceIndex + 1;
        foreach (var a in musicSlots)
        {
            args.Add("-t"); args.Add(F(Math.Max(0.5, a.Duration)));
            args.Add("-i"); args.Add(a.Path);
            audioInputs.Add((a, nextInput));
            nextInput++;
        }

        fc.Append($"[{silenceIndex}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[abase];\n");
        var audioLabels = new List<string> { "abase" };

        for (var i = 0; i < clips.Count; i++)
        {
            if (clips[i].Kind != MediaKind.Video || !clips[i].HasAudio) continue;
            var delayMs = (long)Math.Round(starts[i] * 1000);
            fc.Append($"[{videoInput[i]}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,")
              .Append($"atrim=0:{F(dur[i])},asetpts=PTS-STARTPTS,")
              .Append($"adelay={delayMs}|{delayMs},apad=whole_dur={F(total)}[ac{i}];\n");
            audioLabels.Add($"ac{i}");
        }

        foreach (var (item, mi) in audioInputs)
        {
            var startMs = (long)Math.Round(item.Start * 1000);
            var volExpr = BuildMusicVolume(item, clips, starts, dur);
            var fade = Math.Min(1.0, item.Duration / 2);
            fc.Append($"[{mi}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,")
              .Append($"atrim=0:{F(item.Duration)},asetpts=PTS-STARTPTS,")
              .Append($"afade=t=in:st=0:d={F(fade)},")
              .Append($"afade=t=out:st={F(Math.Max(0, item.Duration - fade))}:d={F(fade)},")
              .Append($"volume='{volExpr}':eval=frame,")
              .Append($"adelay={startMs}|{startMs},apad=whole_dur={F(total)}[am{mi}];\n");
            audioLabels.Add($"am{mi}");
        }

        if (audioLabels.Count > 1)
        {
            var mixIn = string.Concat(audioLabels.Select(l => $"[{l}]"));
            fc.Append($"{mixIn}amix=inputs={audioLabels.Count}:duration=first:dropout_transition=0:normalize=0,")
              .Append("alimiter=limit=0.95[aout];\n");
        }
        else
        {
            fc.Append("[abase]anull[aout];\n");
        }

        args.Add("-c:v"); args.Add("copy");
        args.Add("-map"); args.Add("0:v");
        args.Add("-map"); args.Add("[aout]");
        args.Add("-c:a"); args.Add("aac");
        args.Add("-b:a"); args.Add("192k");
        args.Add("-ar"); args.Add("48000");
        args.Add("-movflags"); args.Add("+faststart");
        args.Add("-progress"); args.Add("pipe:1");
        args.Add("-nostats");
        args.Add("-t"); args.Add(F(total));
        args.Add(opt.OutputPath);

        var plan = new Plan(args, fc.ToString(), total, "ffmpeg -f concat … 拼接+混音");
        await RunPlanAsync(plan, opt, total, 0, total, "拼接混音 ", progress, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 构建

    /// <summary>构建完整导出计划（预览/兼容入口）：准备素材 + 核心计划。</summary>
    public Plan BuildPlan(ProjectFile project, ExportOptions opt)
    {
        int W = opt.Width - opt.Width % 2;
        int H = opt.Height - opt.Height % 2;
        PrepareClips(project, W, H);
        return BuildPlanCore(project, opt, opt.OutputPath, false, null, null);
    }

    /// <summary>导出前的素材统一准备（就地修改，幂等）：动图截帧缓存重建、旧检测数据重检、运镜参数重算。</summary>
    private void PrepareClips(ProjectFile project, int W, int H)
    {
        // 旧工程可能只有单曲 MusicPath / AudioItems：迁移成 MusicPaths，保证无头导出与界面一致
        project.MigrateLegacyMusic();

        var clips = project.Clips;
        // 动图片段（小于 2 秒的视频按图片入轨）的截帧缓存可能被清理：导出前用原始视频重建，
        // 否则 ffmpeg 会读到不存在的文件。这里没有 UI，可直接改路径。
        foreach (var (clip, still) in MediaSource.RebuildMissingStills(clips))
            clip.SourcePath = still;

        // 旧工程（MotionV<3）的运镜参数来自上一版策略（手动运镜/统一缩放下移焦点）。
        // 导出前按"按素材自动取景"重算，保证与预览一致。
        // 旧检测数据（FaceHV<2，Haar 时代的误检/偏移框）先用 YuNet 重检 —— 检测质量
        // 直接决定取景，宁可花每张约 1 秒的同步检测也不能用过期数据。
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            if (c.Kind != MediaKind.Image) continue;
            var reDetected = false;
            if (c.FaceHV < 2 && File.Exists(c.SourcePath))
            {
                var r = new FaceDetector().Detect(MediaSource.Resolve(c.SourcePath), c.Rotation);
                c.FaceX = r.X;
                c.FaceY = r.Y;
                c.FaceSY = Math.Clamp(r.Y, 0.01, 0.99);
                c.FaceH = CoverMath.FaceHeightToCanvas(r.FaceHeight, c.SourceWidth, c.SourceHeight, W, H);
                c.FaceHV = 2;
                c.FaceDetected = r.FaceFound;
                c.FaceDiag = $"{r.Method}/{r.Status}";
                reDetected = true;
            }
            if (c.MotionV < 3 || reDetected)
                MotionPresets.Apply(c, i, W, H);
        }
    }

    /// <summary>帧对齐后的时长、转场时长与时间轴起点。firstDurOverride：非首块首片段裁掉进入转场后的时长。</summary>
    private static void ComputeTiming(List<Clip> clips, int fps, double? firstDurOverride,
        out int[] frames, out double[] dur, out double[] td, out double[] starts, out double total)
    {
        var n = clips.Count;
        frames = new int[n];
        dur = new double[n];
        for (var i = 0; i < n; i++)
        {
            var d = i == 0 && firstDurOverride is { } fo ? fo : clips[i].Duration;
            frames[i] = Math.Max(2, (int)Math.Round(d * fps));
            dur[i] = frames[i] / (double)fps;
        }
        starts = new double[n];
        td = new double[n];
        for (var i = 0; i < n; i++)
        {
            if (i == 0) { starts[0] = 0; td[0] = 0; continue; }
            var prev = clips[i - 1];
            var limit = Math.Min(dur[i - 1], dur[i]) * 0.8;
            td[i] = clips[i].Transition == TransitionKind.None
                ? 0
                : Math.Min(clips[i].TransitionDuration, limit);
            // 转场时长对齐到整帧，避免音画漂移
            td[i] = Math.Floor(td[i] * fps) / fps;
            starts[i] = starts[i - 1] + dur[i - 1] - td[i];
        }
        total = n > 0 ? starts[n - 1] + dur[n - 1] : 0;
    }

    /// <summary>
    // ==================================================================== 图片体检

    /// <summary>
    /// 导出前给全部图片片段做解码体检：返回 [(片段下标, 灰帧替换路径)]。
    /// 检查与缓存由 ImageHealth 统一提供（导入时已检过的直接命中缓存，零开销）。
    /// </summary>
    private async Task<List<(int Index, string Placeholder)>> PreValidateImagesAsync(
        List<Clip> clips, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        LastSwappedFiles.Clear();
        var imageIdx = new List<int>();
        for (var i = 0; i < clips.Count; i++)
            if (clips[i].Kind == MediaKind.Image) imageIdx.Add(i);
        if (imageIdx.Count == 0) return new();

        var badPaths = await ImageHealth.CheckManyAsync(
            imageIdx.Select(i => clips[i].SourcePath),
            (done, total) => progress?.Report(new ExportProgress(0, $"正在检查图片素材 {done}/{total}…", 0)),
            ct).ConfigureAwait(false);
        if (badPaths.Count == 0) return new();

        // 损坏图 → 生成同尺寸灰帧
        var result = new List<(int, string)>();
        foreach (var i in imageIdx)
        {
            if (!badPaths.Contains(clips[i].SourcePath)) continue;
            var ph = await ImageHealth.GetPlaceholderAsync(
                clips[i].SourceWidth, clips[i].SourceHeight, clips[i].SourcePath, ct).ConfigureAwait(false);
            if (ph is null) continue;
            result.Add((i, ph));
            LastSwappedFiles.Add(clips[i].SourcePath);
        }
        if (result.Count > 0)
        {
            var names = string.Join("、", result.Select(r => Path.GetFileName(clips[r.Item1].SourcePath)).Take(3));
            var more = result.Count > 3 ? $" 等 {result.Count} 张" : "";
            var msg = $"发现 {result.Count} 张损坏图片，已用灰帧替代继续导出：{names}{more}";
            AppendExportLog("== 图片体检：" + msg + " ==\n" +
                            string.Join("\n", LastSwappedFiles) + "\n");
            progress?.Report(new ExportProgress(0, msg, 0));
        }
        return result;
    }

    /// <summary>分块边界（含首尾；中间边界的进入转场不能吃掉过多自身时长，保证下一块首片段帧数充足）。
    /// 块大小按输出分辨率自适应：滤镜队列约每片段缓存 24 帧，块内峰值内存控制在 ~2.4GB。
    /// </summary>
    private static List<int> ComputeChunkBounds(double[] dur, double[] td, int n, int W, int H)
    {
        var frameBytes = W * H * 1.5;                       // yuv420p
        var maxClips = (int)Math.Clamp(2_400_000_000.0 / (frameBytes * 24), 12, ChunkMaxClips);

        var bounds = new List<int> { 0 };
        var last = 0;
        while (n - 1 - last > maxClips)
        {
            var b = Math.Min(last + maxClips, n - 2);
            while (b > last + 1 && td[b] > dur[b] * 0.5) b--;
            bounds.Add(b);
            last = b;
        }
        bounds.Add(n - 1);
        return bounds;
    }

    private Plan BuildPlanCore(ProjectFile project, ExportOptions opt, string outputPath,
        bool videoOnly, double? outDuration, double? firstDurOverride, double firstMotionOffset = 0)
    {
        int W = opt.Width - opt.Width % 2;
        int H = opt.Height - opt.Height % 2;
        var fps = opt.Fps;

        var clips = project.Clips.ToList();
        var n = clips.Count;

        // ---- 1. 帧对齐后的时长与时间轴起点 ----
        ComputeTiming(clips, fps, firstDurOverride, out var frames, out var dur, out var td, out var starts, out var total);

        // 非首块的首片段：上一块已渲染它进入转场的 firstMotionOffset 秒，本块从该时刻继续。
        // 时长被 firstDurOverride 裁短的同时，运镜也要从 td 时刻继续——zoompan 按完整帧数
        // 铺满后用 trim 裁掉前 td 秒（进度自然衔接），否则接缝处缩放/滑动会跳回起点。
        var hasFirstOffset = firstMotionOffset > 0.001;
        var firstFramesOrig = frames[0] + (int)Math.Round(firstMotionOffset * fps);

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var fc = new StringBuilder();

        // ---- 2. 输入 ----
        // 每张照片各占一路 -i。**绝不能**再把照片拼成一路 concat 输入：
        // 竖幅/横幅混排时清单流内的帧尺寸会中途变化，共享滤镜图每变一次就整体重建
        // （"Reconfiguring filter graph because video parameters changed…"），
        // 与 fsync/xfade 的分支并存时会直接死锁——表现为导出进度条不动、输出文件
        // 大小恒定（实测 23 张里只多一张 3000×4000 竖图即可复现）。
        // 命令行长度由"分块渲染"兜住：单条命令的片段数上限 ChunkMaxClips，
        // 60 个路径约 8~12KB，远低于 Windows 32767 字符的上限。
        var inputCount = 0;
        var photoInput = new Dictionary<int, int>();
        var videoInput = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
        {
            var c = clips[i];
            if (c.Kind == MediaKind.Image)
            {
                args.Add("-i"); args.Add(MediaSource.Resolve(c.SourcePath));
                photoInput[i] = inputCount++;
                continue;
            }
            // 视频必须逐个输入：各自带 -ss/-t 节选。
            // 非首块的首片段：上一块已播过进入转场的 firstMotionOffset 秒，-ss 要跳过它。
            // 起点与时长都要按元数据钳制：请求越过文件末尾时输入提前 EOF，
            // 滤镜图分支（xfade 树）会饿死，ffmpeg 报"Conversion failed!"退出码 69（AVERROR_EOF 低字节）
            var ssIn = i == 0 ? c.SourceIn + firstMotionOffset : c.SourceIn;
            ssIn = Math.Max(0, Math.Min(ssIn, c.SourceDuration - 0.04));
            var tReq = Math.Min(dur[i], Math.Max(0.04, c.SourceDuration - ssIn - 0.04));
            args.Add("-ss"); args.Add(F(ssIn));
            args.Add("-t"); args.Add(F(tReq));
            args.Add("-i"); args.Add(c.SourcePath);
            videoInput[i] = inputCount++;
        }

        var silenceIndex = inputCount;
        List<(AudioItem Item, int Index)> audioInputs = new();
        if (!videoOnly)
        {
            args.Add("-f"); args.Add("lavfi");
            args.Add("-t"); args.Add(F(total + 0.5));
            args.Add("-i"); args.Add("anullsrc=r=48000:cl=stereo");

            // 音频轨：背景音乐按列表顺序循环铺满整条时间轴（每首一首、播完接下一首，
            // 列表尾后回到第一首）。每段 = 一个输入，进出各做淡入/淡出，照片段之外静音。
            var musicSlots = BuildMusicSlots(project, total);
            var nextInput = silenceIndex + 1;   // silenceIndex 是 anullsrc
            foreach (var a in musicSlots)
            {
                args.Add("-t"); args.Add(F(Math.Max(0.5, a.Duration)));
                args.Add("-i"); args.Add(a.Path);
                audioInputs.Add((a, nextInput));
                nextInput++;
            }
        }

        // ---- 3. 画面 ----
        var ss = W <= 2560 ? 2.0 : 1.5;           // 运镜的超采样倍率，抵消裁剪的整数取整抖动
        var sw = (int)(W * ss / 2) * 2;
        var sh = (int)(H * ss / 2) * 2;
        var captionFiles = new List<string>();    // drawtext 的 textfile 临时文件（渲染后清理）

        // 照片输入只有 1 帧：缩放/裁切后就交给 zoompan，由 d=frames 铺成整段

        for (var i = 0; i < n; i++)
        {
            var c = clips[i];
            if (c.Kind == MediaKind.Video)
            {
                // tpad 兜底：探测时长偏大或文件截断导致输入帧数不足时，克隆末帧补齐，
                // 再 trim 裁到精确时长——每个分支的帧数恒等于期望值，xfade 树不会饿死。
                // 正常路径下输入恰为 dur 秒，tpad 追加的 5 秒克隆帧被 trim 丢弃（开销可忽略）。
                fc.Append($"[{videoInput[i]}:v]fps={fps},")
                  .Append($"tpad=stop_mode=clone:stop_duration=5,trim=duration={F(dur[i])},")
                  .Append($"scale={W}:{H}:force_original_aspect_ratio=increase:flags=lanczos,")
                  .Append($"crop={W}:{H},setsar=1,")
                  .Append(CaptionFilters(c, H, captionFiles))
                  .Append($"format=yuv420p,setpts=PTS-STARTPTS[v{i}];\n");
                continue;
            }

            // 字幕 drawtext 链（空串表示该片段无字幕）
            var capFx = CaptionFilters(c, H, captionFiles);

            // 照片输入只有 1 帧：缩放/裁切后交给 zoompan，由 d=frames 铺满整段
            // 用户旋转放在最前面（EXIF 自动转正之后），后续 scale/crop/cover 才按旋转后的宽高计算。
            var photoPrefix = $"[{photoInput[i]}:v]" + RotationFilter(c.Rotation);

            // 非首块的首片段：zoompan 按完整帧数铺满后再裁掉前 td 秒（上一块已渲染），
            // 表达式进度用 on+firstFrames 平移，运动与上一块尾部无缝衔接
            var isOffsetClip = hasFirstOffset && i == 0;
            string? firstTrim = isOffsetClip
                ? $"trim=start={F(firstMotionOffset)}:end={F(firstFramesOrig / (double)fps)},setpts=PTS-STARTPTS,"
                : null;

            // 运镜：z 与焦点随进度缓动；有人脸信息时套用「保留上半身」约束。
            // 偏移片段的 zoompan 按完整帧数铺满（d=firstFramesOrig），进度用自然 on/denom——
            // 后面的 trim 才把前 td 秒裁掉，on 不受 trim 影响，天然从 td 处继续；
            // 不能再在表达式里 +firstFrames（会双重偏移，接缝处进度快进一倍）。
            var denom = isOffsetClip ? firstFramesOrig : frames[i];
            var p = $"min(on/{denom},1)";
            var e = $"(3*{p}*{p}-2*{p}*{p}*{p})";

            // ---- 竖幅照片：取景窗沿原图上下滑动，末尾停在人脸所在横带 ----
            // 用 crop 的逐帧 y 表达式实现（crop 的 w/h 只在初始化时求值，故不做缩放）。
            // 照片输入只有 1 帧：先用 zoompan z=1 复制成 frames 帧序列，crop 的 n 才有逐帧变化。
            if (c.PanV == 1 && c.SourceWidth > 0 && c.SourceHeight > 0)
            {
                var (offX, offY, cw, chh) = CoverMath.Cover(c.SourceWidth, c.SourceHeight, sw, sh);
                var y0 = c.StartFocusY * sh + offY - sh / 2.0;
                var y1 = c.EndFocusY * sh + offY - sh / 2.0;
                // crop 的逐帧变量是 n（zoompan 才是 on），同样用 smoothstep 缓动。
                // trim 放在 crop 之后：crop 的 n 是自身计数器，若 trim 在前面会把 n 归零
                var pc = $"min(n/{denom},1)";
                var ec = $"(3*{pc}*{pc}-2*{pc}*{pc}*{pc})";
                var yExpr = $"clip({F(y0)}+({F(y1 - y0)})*{ec},0,in_h-out_h)";
                var bw = Math.Max(2, (int)Math.Round(cw / 2) * 2);
                var bh = Math.Max(2, (int)Math.Round(chh / 2) * 2);
                var dPan = isOffsetClip ? firstFramesOrig : frames[i];

                fc.Append(photoPrefix)
                  .Append($"scale={bw}:{bh}:flags=lanczos,")
                  .Append($"zoompan=z='1':x='0':y='0':d={dPan}:s={bw}x{bh}:fps={fps},")
                  .Append($"crop={sw}:{sh}:{F(Math.Max(0, offX))}:'{yExpr}',")
                  .Append(firstTrim)
                  // 注意不要在 zoompan 之后再挂 fps= 滤镜：zoompan 已输出规整的 fps 时间戳，
                  // 再过一遍 fps 会把末尾一帧吞掉（实测 120 帧变 119）。
                  .Append($"scale={W}:{H}:flags=lanczos,")
                  .Append($"setsar=1,")
                  .Append(capFx)
                  .Append($"format=yuv420p,setpts=PTS-STARTPTS[v{i}];\n");
                continue;
            }

            var z = $"{F(clips[i].StartZoom)}+({F(clips[i].EndZoom - clips[i].StartZoom)})*{e}";
            var fx = $"{F(c.StartFocusX)}+({F(c.EndFocusX - c.StartFocusX)})*{e}";
            var fy = $"{F(c.StartFocusY)}+({F(c.EndFocusY - c.StartFocusY)})*{e}";

            if (MotionMath.UpperBodyConstraint(c) is { } ub)
                z = $"min({z},{F(ub.MaxZoom)})";

            // 旧工程（MotionV<2）的焦点曾被下移 0.9 头高，这里把基准搬回人脸中心；
            // 新片段（MotionV=2）本身就是人脸居中取景，不再做任何偏移。
            if (c.MotionV < 2 && c.FaceH > 0.01)
            {
                var oldCenter = Math.Clamp(c.FaceY + Math.Min(c.FaceH * MotionMath.LegacyBiasFactor, 0.35), 0.05, 0.95);
                fy = $"min(max(({fy})+{F(c.FaceY - oldCenter)},0),1)";
            }

            var x = $"min(max(({fx})*iw-iw/({z})/2,0),iw-iw/({z}))";
            var y = $"min(max(({fy})*ih-ih/({z})/2,0),ih-ih/({z}))";

            // 关键：zoompan 的内置缩放器（非 lanczos）会把清晰度拉低约 12%，
            // 是"码流高但画面糊"的根因。让 zoompan 输出到超采样分辨率（z=1 时恒等、
            // z>1 时把取景窗放大到 sw×sh），再用 lanczos 一次性缩到目标分辨率，
            // 全链路的重采样都走 lanczos，实测与理想缩放逐像素一致。
            fc.Append(photoPrefix)
              .Append($"scale={sw}:{sh}:force_original_aspect_ratio=increase:flags=lanczos,")
              .Append($"crop={sw}:{sh},")
              .Append($"zoompan=z='{z}':x='{x}':y='{y}':d={(isOffsetClip ? firstFramesOrig : frames[i])}:s={sw}x{sh}:fps={fps},")
              .Append(firstTrim)
              .Append($"scale={W}:{H}:flags=lanczos,")
              .Append($"setsar=1,")
              .Append(capFx)
              .Append($"format=yuv420p,setpts=PTS-STARTPTS[v{i}];\n");
        }

        // ---- 4. 转场 / 拼接 ----
        // 平衡二叉树拼接：链式 xfade 在上千片段时每帧要穿透几百层滤镜，调度开销
        // 超线性增长（实测 100 张 30s / 300 张 142s）。树形结构与链式数学等价：
        // 相邻两组 [A][B] 拼接时，接缝转场 = B 首片段的进入转场（时长 td），偏移
        // = A 的长度 - td —— 与链式在同一时刻发生同一转场（转场互不重叠，可结合）。
        // 节点：(标签, 覆盖时长, 首片段下标)。首片段下标用于取接缝转场类型。
        var level = new List<(string Label, double Len, int First)>();
        for (var i = 0; i < n; i++)
            level.Add(($"v{i}", dur[i], i));

        var pass = 0;
        while (level.Count > 1)
        {
            pass++;
            var nextLevel = new List<(string Label, double Len, int First)>();
            for (var k = 0; k + 1 < level.Count; k += 2)
            {
                var (la, laLen, _) = level[k];
                var (lb, lbLen, lbFirst) = level[k + 1];
                var merged = $"m{pass}_{k}";
                var junctionTd = td[lbFirst];   // 接缝处 = 右组首片段的进入转场
                if (junctionTd > 0.001)
                {
                    fc.Append($"[{la}][{lb}]xfade=transition={clips[lbFirst].Transition.ToXfade()}")
                      .Append($":duration={F(junctionTd)}:offset={F(laLen - junctionTd)}[{merged}];\n");
                }
                else
                {
                    // concat 输出时间基固定为 1/1000000；若上层与 xfade 分支（时间基 1/fps）拼接，
                    // ffmpeg 会因两侧 timebase 不一致报 "do not match ... xfade timebase" 而中断导出。
                    // 这里把 concat 输出规整回 1/fps，与所有 v{i}/xfade 节点保持一致。
                    fc.Append($"[{la}][{lb}]concat=n=2:v=1:a=0,settb=expr=1/{fps}[{merged}];\n");
                }
                nextLevel.Add((merged, laLen + lbLen - junctionTd, level[k].First));
            }
            if (level.Count % 2 == 1)
                nextLevel.Add(level[^1]);
            level = nextLevel;
        }
        fc.Append($"[{level[0].Label}]format=yuv420p[vout];\n");

        // ---- 5. 声音：原声 + 音乐并行混合（分块渲染的纯视频块跳过） ----
        if (!videoOnly)
        {
            fc.Append($"[{silenceIndex}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[abase];\n");
            var audioLabels = new List<string> { "abase" };

            for (var i = 0; i < n; i++)
            {
                var c = clips[i];
                if (c.Kind != MediaKind.Video || !c.HasAudio) continue;
                var delayMs = (long)Math.Round(starts[i] * 1000);
                fc.Append($"[{videoInput[i]}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,")
                  .Append($"atrim=0:{F(dur[i])},asetpts=PTS-STARTPTS,")
                  .Append($"adelay={delayMs}|{delayMs},apad=whole_dur={F(total)}[ac{i}];\n");
                audioLabels.Add($"ac{i}");
            }

            foreach (var (item, mi) in audioInputs)
            {
                var startMs = (long)Math.Round(item.Start * 1000);
                var volExpr = BuildMusicVolume(item, clips, starts, dur);

                // 每段音乐的进出入淡入淡出（各 1 秒；段太短时取一半）
                var fade = Math.Min(1.0, item.Duration / 2);

                fc.Append($"[{mi}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,")
                  .Append($"atrim=0:{F(item.Duration)},asetpts=PTS-STARTPTS,")
                  .Append($"afade=t=in:st=0:d={F(fade)},")
                  .Append($"afade=t=out:st={F(Math.Max(0, item.Duration - fade))}:d={F(fade)},")
                  .Append($"volume='{volExpr}':eval=frame,")
                  .Append($"adelay={startMs}|{startMs},apad=whole_dur={F(total)}[am{mi}];\n");
                audioLabels.Add($"am{mi}");
            }

            if (audioLabels.Count > 1)
            {
                var mixIn = string.Concat(audioLabels.Select(l => $"[{l}]"));
                fc.Append($"{mixIn}amix=inputs={audioLabels.Count}:duration=first:dropout_transition=0:normalize=0,")
                  .Append("alimiter=limit=0.95[aout];\n");
            }
            else
            {
                fc.Append("[abase]anull[aout];\n");
            }
        }

        // ---- 6. 编码参数（统一软编码：AVC→libx264，HEVC→libx265） ----
        // 硬编码在运动画面上明显偏软（实测 NVENC 保留 84% 细节 vs libx264 99%），
        // 因此只提供软编码，画质优先。CRF 数值两种编码器通用。
        if (opt.Codec == VideoCodec.Hevc)
        {
            args.Add("-c:v"); args.Add("libx265");
            args.Add("-preset"); args.Add("medium");
            args.Add("-crf"); args.Add(opt.Crf.ToString(CultureInfo.InvariantCulture));
            // x265 默认把详细统计刷到 stderr，关闭日志避免污染导出诊断；错误仍可见
            args.Add("-x265-params"); args.Add("log-level=error");
            // HEVC 默认 tag 是 hev1（参数集可在码流内）；hvc1 把参数集固定在容器头，
            // iPhone/Mac 等 Apple 设备只认 hvc1，家庭视频要在多种设备播放，统一打成 hvc1。
            // libx265 已把参数集写入 extradata，加此标记安全（ffprobe 验证 codec_tag_string=hvc1）。
            args.Add("-tag:v"); args.Add("hvc1");
        }
        else
        {
            args.Add("-c:v"); args.Add("libx264");
            args.Add("-preset"); args.Add("medium");
            args.Add("-crf"); args.Add(opt.Crf.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-pix_fmt"); args.Add("yuv420p");
        // HEVC 的 profile 名与 H.264 不同：main 对应 8bit 4:2:0（等价于 H.264 high 的常用场景）
        args.Add("-profile:v"); args.Add(opt.Codec == VideoCodec.Hevc ? "main" : "high");
        args.Add("-level"); args.Add(LevelFor(W, H, fps));
        if (!videoOnly)
        {
            args.Add("-c:a"); args.Add("aac");
            args.Add("-b:a"); args.Add("192k");
            args.Add("-ar"); args.Add("48000");
        }
        args.Add("-movflags"); args.Add("+faststart");
        args.Add("-map"); args.Add("[vout]");
        if (!videoOnly) { args.Add("-map"); args.Add("[aout]"); }
        args.Add("-progress"); args.Add("pipe:1");
        args.Add("-nostats");
        args.Add("-t"); args.Add(F(outDuration ?? total));
        args.Add(outputPath);

        var preview = "ffmpeg " + string.Join(" ", args.Take(12)) + " ... " + outputPath;
        return new Plan(args, fc.ToString(), outDuration ?? total, preview, captionFiles);
    }

    /// <summary>
    /// 配乐音量表达式：只在显示照片的区间出声（视频段一律静音），
    /// 淡入淡出的平滑由 afade 负责，这里只做区间的开关。
    /// 注意：该滤镜作用于 atrim+asetpts 之后的音乐段本地时间（从 0 计），
    /// 所以视频段的时间轴区间必须先平移 -item.Start 换算成本地区间。
    /// </summary>
    private static string BuildMusicVolume(AudioItem item, List<Clip> clips, double[] starts, double[] dur)
    {
        var vol = F(item.Volume);
        var segs = new List<string>();
        for (var i = 0; i < clips.Count; i++)
        {
            if (clips[i].Kind != MediaKind.Video) continue;
            // 视频段 [starts[i], starts[i]+dur[i]] 与音乐段 [item.Start, item.End] 求交
            var a = Math.Max(starts[i], item.Start);
            var b = Math.Min(starts[i] + dur[i], item.End);
            if (b - a < 0.05) continue;                       // 不相交：该音乐段不受此视频段影响
            segs.Add($"between(t,{F(a - item.Start)},{F(b - item.Start)})");
        }
        if (segs.Count == 0) return vol;

        return $"{vol}*not(gt({string.Join("+", segs)},0))";
    }

    /// <summary>
    /// 生成背景音乐的时间轴分段：按 MusicPaths 顺序逐首铺满 [0, total)，
    /// 列表播完回到第一首（多曲循环）。每首只播自身时长，最后一段截到片尾。
    /// </summary>
    private static List<AudioItem> BuildMusicSlots(ProjectFile project, double total)
    {
        var slots = new List<AudioItem>();
        var paths = project.MusicPaths
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0 || total < 1.0) return slots;

        var t = 0.0;
        var idx = 0;
        while (t < total - 0.4)
        {
            var path = paths[idx % paths.Count];
            idx++;

            double songDur;
            try
            {
                var probe = MediaProbe.ProbeAsync(path).GetAwaiter().GetResult();
                songDur = probe?.Duration ?? 0;
            }
            catch { songDur = 0; }
            if (songDur <= 0.5) songDur = total - t;   // 探测失败就让它播到片尾（ffmpeg 会自然停）

            var slotDur = Math.Min(songDur, total - t);
            if (slotDur < 0.5) break;

            slots.Add(new AudioItem
            {
                Path = path,
                Start = t,
                Duration = slotDur,
                SourceDuration = songDur,
                Volume = project.MusicVolume
            });
            t += slotDur;
        }
        return slots;
    }

    /// <summary>用户旋转 → ffmpeg 滤镜（顺时针；叠加在 ffmpeg 自动 EXIF 转正之后）。</summary>
    private static string RotationFilter(int rotation) => rotation switch
    {
        90  => "transpose=clock,",
        180 => "transpose=clock,transpose=clock,",
        270 => "transpose=cclock,",
        _   => "",
    };

    private static string LevelFor(int w, int h, int fps)
    {
        var pixels = (long)w * h;
        if (pixels > 1920L * 1080) return fps > 30 ? "5.2" : "5.1";
        return "4.2";
    }
}
