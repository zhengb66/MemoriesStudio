using System.Windows;

namespace MemoriesStudio.Views;

/// <summary>关于对话框：软件名称 / 版本号 / 功能简介（滚动）/ 作者 / 版权。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        TxtVersion.Text = ProductInfo.VersionText;
        TxtDescription.Text = ProductInfo.Description;
        TxtAuthor.Text = ProductInfo.Author;
        TxtCopyright.Text = ProductInfo.Copyright;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
