using System.Runtime.InteropServices;

namespace SubtitleMerger.App.Services;

/// <summary>
/// WinUI 3 的 Window 没有「最小尺寸」属性（原 Tk 的 root.minsize(1120, 800) 无原生对应）。
/// 这里通过 comctl32 的窗口子类化拦截 WM_GETMINMAXINFO 设置 ptMinTrackSize，
/// 不替换任何控件，也不改变 WinUI 的消息循环。
///
/// 缓冲区安全：MINMAXINFO 是从 32 位到 64 位都「恰好对齐」的结构（全 int），
/// 但子类回调里的 lParam 由系统提供，必须按指针大小解析，不能依赖 Marshal 默认行为。
/// </summary>
internal static class NativeWindowHelper
{
    private const uint WmGetMinMaxInfo = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 PtReserved;
        public Point32 PtMaxSize;
        public Point32 PtMaxPosition;
        public Point32 PtMinTrackSize;
        public Point32 PtMaxTrackSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam,
                                         IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc,
                                                  UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg,
                                                  IntPtr wParam, IntPtr lParam);

    // 必须长期持有委托，防止 GC 回收后回调失效
    private static SubclassProc? _callback;
    private static IntPtr _hwnd;
    private static int _minWidth = 900;
    private static int _minHeight = 600;

    public static void SetMinimumSize(IntPtr hwnd, int width, int height)
    {
        _minWidth = Math.Max(320, width);
        _minHeight = Math.Max(240, height);
        if (_callback is not null && _hwnd == hwnd) return;
        if (_callback is not null && _hwnd != IntPtr.Zero)
        {
            try { RemoveWindowSubclass(_hwnd, _callback, UIntPtr.Zero); } catch { /* 忽略 */ }
        }
        _hwnd = hwnd;
        _callback = SubclassWndProc;
        try { SetWindowSubclass(hwnd, _callback, UIntPtr.Zero, IntPtr.Zero); }
        catch { /* 子类化失败只影响最小尺寸，不影响功能 */ }
    }

    private static IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam,
                                          IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (uMsg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            try
            {
                MinMaxInfo mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                mmi.PtMinTrackSize.X = _minWidth;
                mmi.PtMinTrackSize.Y = _minHeight;
                Marshal.StructureToPtr(mmi, lParam, false);
            }
            catch
            {
                // 回调里绝不能抛异常：一旦逃逸到消息循环就是 0xc000027b（不可恢复的 UI 崩溃）
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
