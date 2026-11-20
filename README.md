# Memories Studio

把照片和视频做成带运镜与转场的影片 —— 一个面向 Windows 的桌面视频编辑器，参考 MemoriesOnTV 的使用方式，
但取景（运镜）以人脸为中心自动生成，导出走 FFmpeg 管线，支持 1080p / 2K / 4K。

作者：郑兵 · 当前版本：0.7.4 · 许可证：MIT

## 功能

- **素材库**：拖入图片 / 视频 / 文件夹批量导入；缩略图与探测信息按需加载（LruCache + 视口裁剪绘制），
  上万张素材也不卡。
- **人脸中心运镜**：YuNet（OpenCV DNN）检测人脸，自动生成横推 / 缓推 / 上下滑动等运镜，按画幅与人脸位置选择策略；
  竖幅素材滑到人脸横带，无人脸时保持 cover 静止。
- **转场**：交叉淡化等转场按时间轴自动平衡成二叉树，避免链式叠加导致的超线性耗时。
- **时间轴**：拖动排序、按落点插入、片段裁剪、照片时长 / 运镜 / 转场逐片段调整。
- **背景音乐**：多曲目顺序循环铺满全片，1 秒淡入淡出，仅照片段出声，可对原声做 duck。
- **预览**：LibVLC 播放（有声）+ OpenCV / ffmpeg 抓帧，暂停态按帧微调。
- **导出**：分块渲染（按分辨率自适应每块片段数）→ `-f concat` 无损拼接 → 终段混音；
  中断/坏图可定位并重渲该块，损坏图片自动用灰帧替代，不会整片失败。
- **工程文件**：`.msproj`（JSON）双击打开，安装包自动注册文件关联。
- **安装包**：WinForms 自包含单文件安装器，内置 FFmpeg / LibVLC / .NET 运行库，免管理员（HKCU）。

## 环境要求

- Windows 10 / 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- FFmpeg（导出用）：安装版自带；源码运行需把 `ffmpeg.exe` 放到程序目录、`ffmpeg\ffmpeg.exe`，
  或加入 `PATH`（也可用环境变量 `FFMPEG_PATH` 指定）

## 构建

```bash
# 主程序（Debug 运行）
dotnet build src/MemoriesStudio.App/MemoriesStudio.App.csproj -c Debug
dotnet run --project src/MemoriesStudio.App/MemoriesStudio.App.csproj

# 发布单文件（自包含，无需目标机装 .NET）
dotnet publish src/MemoriesStudio.App/MemoriesStudio.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibraries=true -p:DebugType=none \
  -o build/publish
```

发布目录里还需要 `libvlc/win-x64` 与 `ffmpeg/ffmpeg.exe`（NuGet 包 `VideoLAN.LibVLC.Windows` 会带来 libvlc；
FFmpeg 请自行下载 gyan.dev 的 essentials 构建）。YuNet 人脸模型已随仓库提供：
`assets/face_detection_yunet_2023mar.onnx`，运行时放到程序目录的 `models/` 下即可（缺失时会尝试下载到用户目录）。

## 打安装包

```bash
# 1) 准备载荷目录：publish 产物全部 + ffmpeg\ffmpeg.exe
# 2) 压成安装器内置载荷
python tools/make_payload.py build/payload src/MemoriesStudio.Setup/payload.zip

# 3) 发布安装器单文件
dotnet publish src/MemoriesStudio.Setup/MemoriesStudio.Setup.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibraries=true -p:DebugType=none \
  -o installer_out
```

安装器支持命令行：

| 参数 | 说明 |
| --- | --- |
| `--install <目录> [--no-desktop] [--no-assoc]` | 静默安装 |
| `--uninstall [--quiet]` | 卸载（工程与素材不受影响） |
| `--selftest [报告路径]` | 安装 → 校验文件/注册表/快捷方式 → 卸载，25 项全链路自检 |
| `--uitest` / `--uishot` | 完成页交互回归 / 截图 |

> 注意：`--selftest` 结束会清掉 HKCU 里的 `.msproj` 关联与快捷方式，跑完需重新静默安装恢复。

## 主程序自检入口

```bash
MemoriesStudio.exe --export <工程.msproj> <输出.mp4> [宽 高 fps crf]   # 无头导出
MemoriesStudio.exe --probe <媒体文件>                                # 探测信息
MemoriesStudio.exe --open-test / --inspect-test / --about-test       # 打开工程 / 结构 / 关于
```

日志写在 `%TEMP%\memories_*.log`。

## 技术栈

| 领域 | 选型 |
| --- | --- |
| 界面 | WPF (.NET 8) |
| 预览 | LibVLCSharp（播放）、OpenCV / FFmpeg（抓帧） |
| 人脸检测 | OpenCV DNN + YuNet（`face_detection_yunet_2023mar.onnx`） |
| 导出 | FFmpeg（libx264，CPU 优先；可选 NVENC / QSV / AMF 探测） |
| 安装器 | WinForms 自包含单文件 + 内置 payload.zip |

架构与踩坑记录见 [`docs/开发文档.md`](docs/开发文档.md)。

## 许可证

MIT，详见 [LICENSE](LICENSE)。
