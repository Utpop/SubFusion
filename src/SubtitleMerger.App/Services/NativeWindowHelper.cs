using System.Runtime.InteropServices;

namespace SubtitleMerger.App.Services;

/// <summary>
/// WinUI 3 的 Window 没有「最小尺寸」属性（原 Tk 的 root.minsize(1120, 800) 无原生对应）。
/// 这里通过 comctl32 的窗口子类化拦截 WM_GETMINMAXINFO 设置 ptMinTrackSize，
/// 不替换任何控件，也不改变 WinUI 的消息循环。
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
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg,
                                                  IntPtr wParam, IntPtr lParam);

    // 必须长期持有委托，防止 GC 回收后回调失效
    private static SubclassProc? _callback;
    private static int _minWidth = 1120;
    private static int _minHeight = 800;

    public static void SetMinimumSize(IntPtr hwnd, int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
        _callback = SubclassWndProc;
        SetWindowSubclass(hwnd, _callback, UIntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam,
                                          IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (uMsg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            MinMaxInfo mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            mmi.PtMinTrackSize.X = _minWidth;
            mmi.PtMinTrackSize.Y = _minHeight;
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
