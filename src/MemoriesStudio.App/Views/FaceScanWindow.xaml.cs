using System.Threading;
using System.Windows;

namespace MemoriesStudio.Views;

/// <summary>
/// 批量人脸识别的进度窗。主窗口在识别期间被禁用（"等全部识别再进行操作"），
/// 本窗保持可点，允许用户取消剩余识别（未识别的片段按默认取景）。
/// </summary>
public partial class FaceScanWindow : Window
{
    /// <summary>主窗口设置，取消按钮触发。</summary>
    public CancellationTokenSource? Cts { get; set; }

    public FaceScanWindow()
    {
        InitializeComponent();
    }

    /// <summary>更新进度。只在 UI 线程调用。</summary>
    public void Report(int done, int total, string name)
    {
        Bar.Value = total <= 0 ? 0 : done * 100.0 / total;
        TxtInfo.Text = $"{done}/{total} · {name}";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Cts?.Cancel();
        BtnCancel.IsEnabled = false;
        TxtInfo.Text = "正在取消…";
    }
}
