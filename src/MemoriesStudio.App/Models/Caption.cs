using System.Text.Json.Serialization;

namespace MemoriesStudio.Models;

/// <summary>字幕垂直位置（两档）。</summary>
public enum CaptionPos
{
    Bottom,
    Top,
}

/// <summary>
/// 片段内的一段硬字幕：随片段保存与删除（无独立字幕轨），导出时烧录进画面。
/// 时间用片段内的局部秒（0 = 片段开始），与视频节选无关——节选改变后区间自动钳回有效范围。
/// </summary>
public sealed class Caption : ObservableObject
{
    private string _text = "";
    private double _start;
    private double _end = 4.0;
    private double _fontSizePct = 5.0;
    private string _colorPreset = "white";
    private CaptionPos _position = CaptionPos.Bottom;
    private double _fade = 0.4;

    /// <summary>字幕文本（支持换行）。</summary>
    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>出现时间（片段局部秒）。</summary>
    public double Start { get => _start; set => Set(ref _start, Math.Max(0, value)); }

    /// <summary>消失时间（片段局部秒）。</summary>
    public double End { get => _end; set => Set(ref _end, value); }

    /// <summary>字号，单位 = 画面高度的百分比（1080p 下 5% ≈ 54px），跨分辨率自适应。</summary>
    public double FontSizePct { get => _fontSizePct; set => Set(ref _fontSizePct, Math.Clamp(value, 2, 12)); }

    /// <summary>颜色预设键：white / yellow / orange / cyan / black。</summary>
    public string ColorPreset { get => _colorPreset; set => Set(ref _colorPreset, value); }

    public CaptionPos Position { get => _position; set => Set(ref _position, value); }

    /// <summary>淡入淡出时长（秒），0 = 直接出现/消失。</summary>
    public double Fade { get => _fade; set => Set(ref _fade, Math.Clamp(value, 0, 2)); }

    /// <summary>字幕距画面顶/底边的边距 = 画面高度 × 该百分比（预览与导出共用）。</summary>
    public const double EdgeMarginPct = 4.5;

    [JsonIgnore] public bool IsValid => !string.IsNullOrWhiteSpace(Text) && End > Start + 0.05;
    [JsonIgnore] public string RangeText => $"{Start:0.0}–{End:0.0}s";

    /// <summary>
    /// 把区间钳回 [0, maxEnd]（片段时长缩短或视频节选改变后调用），
    /// 保证 end>start：容不下时整个区间靠前贴齐。
    /// </summary>
    public void ClampTo(double maxEnd)
    {
        maxEnd = Math.Max(0.1, maxEnd);
        if (End > maxEnd) End = maxEnd;
        if (Start > maxEnd) Start = maxEnd;
        if (End <= Start)
        {
            var len = Math.Min(0.5, maxEnd);
            Start = Math.Max(0, maxEnd - len);
            End = maxEnd;
        }
    }

    /// <summary>颜色预设 → (fontcolor, 边框色)。</summary>
    public static (string fill, string border) Colors(string preset) => preset switch
    {
        "yellow" => ("#FFE14D", "black"),
        "orange" => ("#FF9A3D", "black"),
        "cyan"   => ("#7FDFFF", "black"),
        "black"  => ("#1A1A1A", "white"),
        _        => ("white", "black"),
    };

    /// <summary>预设键的中文显示名（下拉框用）。</summary>
    public static string PresetLabel(string preset) => preset switch
    {
        "yellow" => "黄色（黑边）",
        "orange" => "橙色（黑边）",
        "cyan"   => "青色（黑边）",
        "black"  => "黑色（白边）",
        _        => "白色（黑边）",
    };
}
