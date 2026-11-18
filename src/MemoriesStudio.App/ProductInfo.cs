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

    /// <summary>关于对话框里的完整功能简介（每段首行缩进两个汉字，段间不空行）。</summary>
    public const string Description =
        "\u3000\u3000本程序是作者使用WorkBuddy开发的习作，目的是将照片和视频快速组织起来，加入音乐和字幕，生成16:9的100%无黑边高清视频，方便您在电视上观看。它以人脸识别为核心卖点，运镜与裁切方式是作者20多年家庭相册制作的总结，有较强的个人色彩，不喜勿喷。\n" +
        "\u3000\u3000核心原则如下：横屏人像照片将以人脸为中心运镜，风景照片则截屏90%后静态显示；竖屏人像照片截取中间部分静态显示，风景照片上下滚动显示90%的内容；视频居中截屏显示。\n" +
        "\u3000\u3000如果人脸识别不准，也可手工选定，还可以通过切换人像和景物的选项，生成不同的运镜效果。\n" +
        "\u3000\u3000背景音乐只在显示静态照片时播放，推荐视频集中放置，以免音乐听感不连贯，推荐插入的每段视频少于30秒，总长度不要超过1小时，以免看起来乏味。";

    public const string Author = "郑兵";

    public const string Copyright = "Copyright © 2026 zhengb66";

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
