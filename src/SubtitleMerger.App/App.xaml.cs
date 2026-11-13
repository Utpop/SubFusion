using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SubtitleMerger.App.Services;
using SubtitleMerger.App.Views;

namespace SubtitleMerger.App;

/// <summary>应用入口。WinUI 3 自动生成 Main 与 Windows App SDK 引导逻辑。</summary>
public partial class App : Application
{
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
        var window = new MainWindow();
        window.Activate();

        // 构建验证钩子：传 --smoke 时，窗口成功加载后写出标记文件并以 0 退出。
        // 用于 CI / 迁移验收，正常双击运行不会触发。
        if (!Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase)))
            return;

        DispatcherQueueTimer timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Tick += (_, _) =>
        {
            string marker = Path.Combine(Path.GetTempPath(), "subtitle_merger_smoke.txt");
            File.WriteAllText(marker, "OK " + DateTime.Now.ToString("O"));
            timer.Stop();
            Environment.Exit(0);
        };
        timer.Start();
    }
}
