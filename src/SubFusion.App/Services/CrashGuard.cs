using System.Runtime.InteropServices;
using System.Text;
using SubFusion.Core;

namespace SubFusion.App.Services;

/// <summary>
/// 崩溃兜底：把未处理异常写进 %TEMP%\subfusion_crash.txt，并在条件允许时弹窗，
/// 而不是让进程「直接消失」。
///
/// 覆盖三类：
/// 1. UI 线程上的托管异常（WinUI 的 Application.UnhandledException）；
/// 2. 后台任务里逃逸的托管异常（TaskScheduler.UnobservedTaskException）；
/// 3. 非 UI 线程上的托管异常（AppDomain.UnhandledException）；
/// 4. 非托管异常（SERH，例如越界访问、OOM 分配失败）—— 记录后让进程正常结束。
/// </summary>
internal static class CrashGuard
{
    private const string FileName = "subfusion_crash.txt";
    private static readonly object Gate = new();
    private static ExceptionFilter? _serhFilter;
    private static bool _installed;

    /// <summary>由 App 在启动时注册：把错误提示投递到 UI 线程，避免后台线程直接弹窗。</summary>
    private static Action<string>? _uiMessageBox;
    public static void SetUiMessageBox(Action<string> show) => _uiMessageBox = show;

    public static string LogPath => Path.Combine(Path.GetTempPath(), FileName);

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Write("UnobservedTask", e.Exception);
        };

        // 诊断开关（SUBFUSION_TRACE=1）：记录所有首次异常，用于定位「异常被 async void 吞掉后
        // 直接 fail-fast」这类没有托管未处理异常现场的崩溃。
        if (Environment.GetEnvironmentVariable("SUBFUSION_TRACE") == "1")
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                Write($"FirstChance({e.Exception.GetType().Name})", e.Exception, firstChance: true);
        }

        try
        {
            // 委托必须长期持有：GetFunctionPointer 形式的 SEH 回调一旦被 GC 回收，
            // 非托管异常发生时就会跳到已释放的 thunk。
            _serhFilter = OnSerh;
            SetUnhandledExceptionFilter(_serhFilter);
        }
        catch { /* 部分受限环境不允许安装过滤器，忽略 */ }
    }

    /// <summary>
    /// 包住 async void 事件处理器：任何异常都在此处吞掉并记录，而不是逃逸到 WinUI 消息循环。
    /// 未捕获异常逃逸到消息循环就是 0xc000027b fail-fast（进程直接消失，无任何提示）。
    /// </summary>
    public static async void FireAndForget(Func<Task> action, string origin)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Write(origin, ex);
            string text = $"{Constants.AppName} 操作失败（已记录到 {LogPath}）：\n\n" +
                          $"{ex.GetType().Name}: {Truncate(ex.Message, 300)}";
            try
            {
                if (_uiMessageBox is not null)
                    _uiMessageBox(text); // 由 UI 线程负责弹窗
                else
                    _ = MessageBoxW(IntPtr.Zero, text, Constants.AppName, 0x00000030 /* MB_ICONWARNING */);
            }
            catch { /* 提示失败也不能再抛 */ }
        }
    }

    /// <summary>WinUI 没有恢复路径时调用：记录并提示用户日志位置。</summary>
    public static void ReportUnhandledAndExit(Exception ex, string origin)
    {
        Write(origin, ex);
        try
        {
            ShowFatalBox(ex, origin);
        }
        catch { /* 弹窗失败也要退出 */ }
        Environment.Exit(2);
    }

    public static void Write(string origin, Exception? ex, bool firstChance = false)
    {
        lock (Gate)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("time   : " + DateTime.Now.ToString("O"));
                sb.AppendLine("origin : " + origin);
                sb.AppendLine("version: " + typeof(CrashGuard).Assembly.GetName().Version);
                sb.AppendLine("proc   : " + Environment.ProcessPath);
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    sb.AppendLine($"memory : ws={p.WorkingSet64 / 1048576}MB private={p.PrivateMemorySize64 / 1048576}MB threads={p.Threads.Count}");
                if (ex is not null) sb.AppendLine(ex.ToString());
                else sb.AppendLine("(no managed exception object)");
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(firstChance ? TracePath : LogPath, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 记录失败不能引发二次崩溃 */ }
        }
    }

    private static string TracePath => Path.Combine(Path.GetTempPath(), "subfusion_trace.txt");

    private static IntPtr OnSerh(IntPtr exceptionPointers)
    {
        try
        {
            Write("SEH", ExceptionFromPointers(exceptionPointers));
        }
        catch { /* 忽略 */ }
        return IntPtr.Zero;   // 继续默认处理：让 WER/进程收尾流程正常进行
    }

    private static Exception ExceptionFromPointers(IntPtr pointers)
    {
        if (pointers == IntPtr.Zero) return new Exception("未知的非托管异常");
        // SetUnhandledExceptionFilter 收到的是 EXCEPTION_POINTERS*，其第一个字段
        // 才指向 EXCEPTION_RECORD；直接把 EXCEPTION_POINTERS 当记录读会拿到无意义的异常码。
        var ep = Marshal.PtrToStructure<ExceptionPointers>(pointers);
        if (ep.ExceptionRecord == IntPtr.Zero) return new Exception("未知的非托管异常");
        var er = Marshal.PtrToStructure<ExceptionRecord>(ep.ExceptionRecord);
        return new Exception($"非托管异常 0x{er.Code:X8} at 0x{er.Address.ToInt64():X} " +
                             $"{DescribeSeverity(er.Code)}");
    }

    private static string DescribeSeverity(uint code) => code switch
    {
        0xC0000005 => "(访问冲突：越界读写 / 空指针)",
        0xC00000FD => "(栈溢出：通常是无限递归)",
        0xE0000001 => "(内存分配失败：进程内存或 GDI/USER 句柄耗尽)",
        0xC0000409 => "(栈缓冲区溢出 / 检测到程序被篡改)",
        _ => "",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public IntPtr ExceptionRecord;
        public IntPtr ContextRecord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public uint Code;
        public uint Flags;
        public IntPtr Record;
        public IntPtr Address;
        public uint NumberParameters;
        public uint Unused;
        public IntPtr Info0;
        public IntPtr Info1;
        public IntPtr Info2;
        public IntPtr Info3;
        public IntPtr Info4;
        public IntPtr Info5;
        public IntPtr Info6;
        public IntPtr Info7;
        public IntPtr Info8;
        public IntPtr Info9;
        public IntPtr Info10;
        public IntPtr Info11;
        public IntPtr Info12;
        public IntPtr Info13;
        public IntPtr Info14;
    }

    private delegate IntPtr ExceptionFilter(IntPtr exceptionPointers);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(ExceptionFilter filter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static void ShowFatalBox(Exception ex, string origin)
    {
        _ = MessageBoxW(IntPtr.Zero,
            $"{Constants.AppName} 遇到未处理的错误，已记录到：\n{LogPath}\n\n" +
            $"类型：{ex.GetType().Name}\n{Truncate(ex.Message, 400)}",
            Constants.AppName + " —— 错误",
            0x00000010 /* MB_ICONERROR */);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : (text.Length > max ? text[..max] + "…" : text);
}
