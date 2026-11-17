using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;
using SubFusion.App.Views;

namespace SubFusion.App;

/// <summary>
/// 应用入口。WinUI 3 自动生成 Main 与 Windows App SDK 引导逻辑。
///
/// 页面通过 <see cref="Shell"/> / <see cref="CurrentWindow"/> 取得外壳与平台服务，
/// 页面自身不持有任何全局状态（所有状态在各自的 ViewModel 中）。
/// </summary>
public partial class App : Application
{
    /// <summary>当前主窗口（单窗口应用）。</summary>
    public static MainWindow? CurrentWindow { get; private set; }

    /// <summary>
    /// 外壳 ViewModel。窗口在构造函数里就要创建第一个页面（Frame 导航早于
    /// <see cref="CurrentWindow"/> 被赋值），因此这里由窗口在构造过程中提前登记，
    /// 页面通过 <see cref="Shell"/> 取到的一定是最新的外壳实例。
    /// </summary>
    public static ShellViewModel? ActiveShell { get; set; }

    /// <summary>外壳 ViewModel，持有五个页面 ViewModel 与导航状态。</summary>
    // 优先使用当前窗口的 ViewModel：窗口重建后不会残留上一次外壳实例。
    public static ShellViewModel? Shell => CurrentWindow?.ViewModel ?? ActiveShell;

    /// <summary>窗口关闭后清掉全局引用，避免页面/服务误用已销毁窗口的外壳。</summary>
    public static void ClearWindow()
    {
        ActiveShell = null;
        CurrentWindow = null;
    }

    /// <summary>把回调投递到当前窗口的 UI 线程；无窗口或队列已关闭时返回 false。</summary>
    public static bool TryRunOnUiThread(Action action)
        => CurrentWindow is not null && CurrentWindow.TryEnqueue(action);

    public App()
    {
        InitializeComponent();
        CrashGuard.Install();
        UnhandledException += (_, e) =>
        {
            // WinUI 对未处理异常默认直接终止进程；这里先落盘再给出可读提示
            e.Handled = false;
            CrashGuard.ReportUnhandledAndExit(e.Exception, "XamlUnhandled");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CurrentWindow = new MainWindow();
        CurrentWindow.Activate();

        // 后台任务的错误提示统一marshal到 UI 线程，避免跨线程弹窗崩溃。
        CrashGuard.SetUiMessageBox(text =>
        {
            TryRunOnUiThread(() => _ = CurrentWindow?.ShowInfoAsync(text));
        });

        // 构建验证钩子：传 --smoke 时，窗口成功加载后写出标记文件并以 0 退出。
        // 用于 CI / 迁移验收，正常双击运行不会触发。
        if (!Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase)))
            return;

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Tick += (_, _) =>
        {
            string marker = Path.Combine(Path.GetTempPath(), "subfusion_smoke.txt");
            File.WriteAllText(marker, "OK " + DateTime.Now.ToString("O"));
            timer.Stop();
            Environment.Exit(0);
        };
        timer.Start();
    }

    /// <summary>自动化复现用的轻量日志：写 %TEMP%subfusion_test.txt（正常启动不写）。</summary>
    public static void LogTest(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "subfusion_test.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
        }
        catch { /* 测试日志失败不影响主流程 */ }
    }
}
