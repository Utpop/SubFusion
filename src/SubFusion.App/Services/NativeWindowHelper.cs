using System.Runtime.InteropServices;

namespace SubFusion.App.Services;

/// <summary>
/// WinUI 3 的 Window 没有「最小尺寸」属性，通过 comctl32 子类化拦截 WM_GETMINMAXINFO。
/// 每个 HWND 独立保存最小尺寸与拖放回调；窗口销毁（WM_NCDESTROY）或显式 RemoveWindow 时
/// 调用 RemoveWindowSubclass，避免回调指向已销毁窗口造成崩溃或跨窗口串状态。
/// </summary>
internal static class NativeWindowHelper
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmDropFiles = 0x0233;
    private const uint WmNcDestroy = 0x0082;

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

    private sealed class WindowState
    {
        public int MinWidth = 900;
        public int MinHeight = 600;
        public SubclassProc? Callback;
        public Action<IReadOnlyList<string>>? OnFilesDropped;
    }

    // 必须长期持有委托，防止 GC 回收后回调失效；按 HWND 分槽，互不干扰。
    private static readonly Dictionary<IntPtr, WindowState> States = new();
    private static readonly object Gate = new();

    private static WindowState EnsureState(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (States.TryGetValue(hwnd, out WindowState? state)) return state;
            state = new WindowState();
            States[hwnd] = state;
            return state;
        }
    }

    public static void SetMinimumSize(IntPtr hwnd, int width, int height)
    {
        WindowState state = EnsureState(hwnd);
        state.MinWidth = Math.Max(320, width);
        state.MinHeight = Math.Max(240, height);
        if (state.Callback is not null) return;
        state.Callback = SubclassWndProc;
        try { SetWindowSubclass(hwnd, state.Callback, UIntPtr.Zero, IntPtr.Zero); }
        catch { /* 子类化失败只影响最小尺寸，不影响功能 */ }
    }

    /// <summary>
    /// 挂接 WM_DROPFILES 兜底拖放。回调按 HWND 保存，避免旧窗口回调被新窗口触发。
    /// </summary>
    public static void SetDropHandler(IntPtr hwnd, Action<IReadOnlyList<string>> onFilesDropped)
    {
        WindowState state = EnsureState(hwnd);
        state.OnFilesDropped = onFilesDropped;
        if (state.Callback is null) SetMinimumSize(hwnd, state.MinWidth, state.MinHeight);
        try { DragAcceptFiles(hwnd, true); } catch { /* 失败则只剩 WinUI 的 OLE 拖放 */ }
    }

    /// <summary>窗口关闭时移除子类化并释放该 HWND 的状态。</summary>
    public static void RemoveWindow(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (!States.TryGetValue(hwnd, out WindowState? state)) return;
            if (state.Callback is not null)
            {
                try { RemoveWindowSubclass(hwnd, state.Callback, UIntPtr.Zero); } catch { /* 忽略 */ }
                state.Callback = null;
            }
            state.OnFilesDropped = null;
            States.Remove(hwnd);
        }
    }

    private static IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam,
                                          IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (uMsg == WmNcDestroy)
        {
            // 窗口销毁是最后一条消息：先移除子类化再交给默认处理，防止回调悬空。
            RemoveWindow(hWnd);
        }
        else if (uMsg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            try
            {
                WindowState state = EnsureState(hWnd);
                MinMaxInfo mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                mmi.PtMinTrackSize.X = state.MinWidth;
                mmi.PtMinTrackSize.Y = state.MinHeight;
                Marshal.StructureToPtr(mmi, lParam, false);
            }
            catch
            {
                // 回调里绝不能抛异常：一旦逃逸到消息循环就是 0xc000027b（不可恢复的 UI 崩溃）
            }
        }
        else if (uMsg == WmDropFiles && wParam != IntPtr.Zero)
        {
            try
            {
                WindowState state = EnsureState(hWnd);
                if (state.OnFilesDropped is not null)
                {
                    state.OnFilesDropped(ReadDropFiles(wParam));
                    return IntPtr.Zero;      // 已处理
                }
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
