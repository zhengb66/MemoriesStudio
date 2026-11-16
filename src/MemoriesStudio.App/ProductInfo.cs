using System.Reflection;
using System.Runtime.InteropServices;

namespace MemoriesStudio;

/// <summary>
/// 产品的展示信息（关于对话框、窗口标题等统一从这里取）。
/// 版本号来自程序集元数据（csproj 的 &lt;Version&gt;），不再手写第二份，避免两处不一致。
/// </summary>
public static class ProductInfo
{
    public const string Name = "Memories Studio";

    /// <summary>一句话功能简介。</summary>
    public const string Tagline = "把散落的照片与视频，自动做成人脸取景、配乐铺满的回忆影片。";

    public const string Author = "郑兵";

    public const string Copyright = "Copyright © 2026 郑兵";

    /// <summary>形如 0.6.0（取 InformationalVersion，退化为程序集版本前三段）。</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // 去掉 SourceLink 附加的 "+sha" 后缀
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            var v = asm.GetName().Version;
            return v is null ? "0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>关于对话框里显示的完整版本行。</summary>
    public static string VersionText => "版本 " + Version;
}
