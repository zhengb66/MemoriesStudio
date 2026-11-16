using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MemoriesStudio.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum MediaKind { Image, Video, Audio }

public enum TransitionKind
{
    None,
    CrossFade,
    FadeBlack,
    WipeLeft,
    WipeRight,
    SlideLeft,
    SlideRight,
    CircleOpen
}

public enum MotionStyle
{
    Auto,
    None,
    ZoomIn,
    ZoomOut,
    PanLeft,
    PanRight,
    PanUp,
    PanDown
}

public static class TransitionInfo
{
    public static string ToDisplay(this TransitionKind k) => k switch
    {
        TransitionKind.None => "无转场",
        TransitionKind.CrossFade => "淡入淡出（交叉溶解）",
        TransitionKind.FadeBlack => "黑场过渡",
        TransitionKind.WipeLeft => "向左擦除",
        TransitionKind.WipeRight => "向右擦除",
        TransitionKind.SlideLeft => "向左推移",
        TransitionKind.SlideRight => "向右推移",
        TransitionKind.CircleOpen => "圆形展开",
        _ => k.ToString()
    };

    public static string ToShort(this TransitionKind k) => k switch
    {
        TransitionKind.None => "—",
        TransitionKind.CrossFade => "溶解",
        TransitionKind.FadeBlack => "黑场",
        TransitionKind.WipeLeft => "左擦",
        TransitionKind.WipeRight => "右擦",
        TransitionKind.SlideLeft => "左推",
        TransitionKind.SlideRight => "右推",
        TransitionKind.CircleOpen => "圆形",
        _ => k.ToString()
    };

    /// <summary>ffmpeg xfade transition name.</summary>
    public static string ToXfade(this TransitionKind k) => k switch
    {
        TransitionKind.CrossFade => "fade",
        TransitionKind.FadeBlack => "fadeblack",
        TransitionKind.WipeLeft => "wipeleft",
        TransitionKind.WipeRight => "wiperight",
        TransitionKind.SlideLeft => "slideleft",
        TransitionKind.SlideRight => "slideright",
        TransitionKind.CircleOpen => "circleopen",
        _ => "fade"
    };

    public static IReadOnlyList<TransitionKind> All { get; } = new[]
    {
        TransitionKind.None, TransitionKind.CrossFade, TransitionKind.FadeBlack,
        TransitionKind.WipeLeft, TransitionKind.WipeRight, TransitionKind.SlideLeft,
        TransitionKind.SlideRight, TransitionKind.CircleOpen
    };

    public static string ToDisplay(this MotionStyle m) => m switch
    {
        MotionStyle.Auto => "自动（人脸为中心）",
        MotionStyle.None => "静止（无运镜）",
        MotionStyle.ZoomIn => "推近",
        MotionStyle.ZoomOut => "拉远",
        MotionStyle.PanLeft => "向左平移",
        MotionStyle.PanRight => "向右平移",
        MotionStyle.PanUp => "向上平移",
        MotionStyle.PanDown => "向下平移",
        _ => m.ToString()
    };

    public static IReadOnlyList<MotionStyle> AllMotions { get; } = new[]
    {
        MotionStyle.Auto, MotionStyle.None, MotionStyle.ZoomIn, MotionStyle.ZoomOut,
        MotionStyle.PanLeft, MotionStyle.PanRight, MotionStyle.PanUp, MotionStyle.PanDown
    };
}
