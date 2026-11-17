using Microsoft.UI.Xaml.Controls;
using SubFusion.App.ViewModels;

namespace SubFusion.App.Pages;

/// <summary>设置页：字段全部直接映射到 AppSettings（即 Core 的 AppConfig）。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Shell?.Settings ?? throw new InvalidOperationException("外壳尚未初始化");
        InitializeComponent();
    }
}
