using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;

namespace SubFusion.App.Pages;

/// <summary>
/// 视频压制页。Code-behind 只做拖放接入：所有参数收集、编码器选择与执行都在 ViewModel + Core 里，
/// 这里不参与任何业务判断。
/// </summary>
public sealed partial class CompressPage : Page
{
    public VideoCompressViewModel ViewModel { get; }

    public CompressPage()
    {
        ViewModel = App.Shell?.Compress ?? throw new InvalidOperationException("外壳尚未初始化");
        InitializeComponent();
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
        => CrashGuard.FireAndForget(() => DropAsync(e), "CompressPage_Drop");

    private async Task DropAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            List<string> paths = items.Select(i => i.Path ?? "").Where(p => p.Length > 0).ToList();
            if (paths.Count == 0) return;
            e.Handled = true;
            ViewModel.AcceptDroppedFile(paths);
        }
        catch { /* 拖放数据异常时静默忽略，按钮选择仍可用 */ }
    }
}
