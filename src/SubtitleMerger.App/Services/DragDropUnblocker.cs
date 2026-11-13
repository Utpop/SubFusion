using System.Runtime.InteropServices;

namespace SubtitleMerger.App.Services;

/// <summary>
/// 让「以管理员权限运行」的程序仍能接收来自普通权限资源管理器的拖放。
///
/// 背景：程序提升到高完整性级别后，Windows 的 UIPI（用户界面特权隔离）会丢弃
/// 低完整性进程发来的窗口消息，其中就包括拖放相关的
/// <c>WM_DROPFILES</c> / <c>WM_COPYDATA</c> / <c>WM_COPYGLOBALDATA</c>。
/// 表现为：从资源管理器往窗口里拖文件，光标显示禁止，程序完全收不到 Drop。
///
/// 处理办法：对目标窗口调用 <c>ChangeWindowMessageFilterEx</c>，把这几条消息显式放行。
/// 这是微软官方给「提升进程需要接收拖放」的标准做法。
/// </summary>
internal static class DragDropUnblocker
{
    // 文件拖放（经典）
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;

    // OLE 拖放（WinUI 走这条）在跨完整性级别时需要的消息
    private const uint WM_DDE_INITIATE = 0x03E0;
    private const uint WM_DDE_TERMINATE = 0x03E1;
    private const uint WM_DDE_REQUEST = 0x03E6;
    private const uint WM_DDE_ACK = 0x03E4;
    private const uint WM_DDE_DATA = 0x03E5;

    private const uint MSGFLT_ALLOW = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ChangeFilterStruct
    {
        public uint CbSize;
        public uint ExtStatus;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message,
                                                           uint action, ref ChangeFilterStruct filter);

    /// <summary>
    /// 放行拖放所需消息。必须在窗口句柄有效之后调用（本项目在 MainWindow 构造里调用）。
    /// 非提升状态下调用也安全，只是没有实际作用。
    ///
    /// 说明：提升进程接收低完整性进程（资源管理器）的拖放，本质是被 UIPI 拦截。
    /// 放行这几个消息后，经典 WM_DROPFILES 与 OLE 拖放的握手消息都能通过，
    /// 再配合 NativeWindowHelper 的 WM_DROPFILES 处理器即可稳定接收文件。
    /// </summary>
    public static bool Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        var filter = new ChangeFilterStruct
        {
            CbSize = (uint)Marshal.SizeOf<ChangeFilterStruct>(),
            ExtStatus = 0,
        };

        uint[] messages =
        {
            WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA,
            WM_DDE_INITIATE, WM_DDE_TERMINATE, WM_DDE_REQUEST, WM_DDE_ACK, WM_DDE_DATA,
        };

        bool any = false;
        foreach (uint message in messages)
        {
            try
            {
                if (ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, ref filter)) any = true;
            }
            catch { /* 个别系统上可能不支持，忽略该条消息 */ }
        }
        return any;
    }
}
