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
    private const uint WmDropFiles = 0x0233;

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint fileIndex, char[]? fileName, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    /// <summary>不调用这个，窗口根本不会收到 WM_DROPFILES。</summary>
    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    // 必须长期持有委托，防止 GC 回收后回调失效
    private static SubclassProc? _callback;
    private static IntPtr _hwnd;
    private static int _minWidth = 900;
    private static int _minHeight = 600;
    private static Action<IReadOnlyList<string>>? _onFilesDropped;

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

    /// <summary>
    /// 挂接 WM_DROPFILES 兜底拖放。
    /// WinUI 自己走 OLE 拖放；在管理员（提升）进程中 OLE 拖放可能被 UIPI 阻断，
    /// 此时资源管理器仍会投递经典的 WM_DROPFILES，用它把文件路径取出来补上。
    /// </summary>
    public static void SetDropHandler(IntPtr hwnd, Action<IReadOnlyList<string>> onFilesDropped)
    {
        _onFilesDropped = onFilesDropped;
        if (_hwnd != hwnd) SetMinimumSize(hwnd, _minWidth, _minHeight);
        try { DragAcceptFiles(hwnd, true); } catch { /* 失败则只剩 WinUI 的 OLE 拖放 */ }
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
        else if (uMsg == WmDropFiles && wParam != IntPtr.Zero && _onFilesDropped is not null)
        {
            try
            {
                _onFilesDropped(ReadDropFiles(wParam));
                return IntPtr.Zero;      // 已处理
            }
            catch
            {
                // 同上：拖放回调里抛异常会连带打穿消息循环
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>从 WM_DROPFILES 的 HDROP 句柄里取出全部拖入路径（并释放句柄）。</summary>
    private static List<string> ReadDropFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        try
        {
            uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFileW(hDrop, i, null, 0);
                if (len == 0) continue;
                var buffer = new char[len + 1];
                uint written = DragQueryFileW(hDrop, i, buffer, (uint)buffer.Length);
                if (written > 0) paths.Add(new string(buffer, 0, (int)written));
            }
        }
        finally
        {
            DragFinish(hDrop);
        }
        return paths;
    }
}
