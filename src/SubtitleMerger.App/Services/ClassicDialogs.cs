using System.Runtime.InteropServices;
using System.Text;

namespace SubtitleMerger.App.Services;

/// <summary>
/// 经典 Win32 文件对话框（IFileOpenDialog / IFileSaveDialog / SHBrowseForFolder）。
///
/// 为什么需要它：WinRT 的 <c>FileOpenPicker</c> / <c>FolderPicker</c> 在**提升（管理员）进程**里
/// 不可用——它们依赖未提升的 Shell 代理，调用时会以 E_ACCESSDENIED (0x80070005) 失败；
/// 在 WinUI 的 <c>async void</c> 命令里这个异常会直接逃逸成 fail-fast（0xc000027b 崩溃）。
/// 经典 Win32 对话框没有这个限制，因此管理员运行时改走这里。
/// </summary>
internal static class ClassicDialogs
{
    #region COM 互操作

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IntPtr shellItem);

    private static Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr DlgOwner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        public string? File;
        public int MaxFile;
        public string? FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefExt;
        public IntPtr CustData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr ReservedPtr;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr Owner;
        public IntPtr Root;
        public string? DisplayName;
        public string? Title;
        public int Flags;
        public IntPtr Callback;
        public IntPtr LParam;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr ptr);

    private const int BIF_RETURNONLYFSDIRS = 0x0001;
    private const int BIF_NEWDIALOGSTYLE = 0x0040;
    private const int BIF_EDITBOX = 0x0010;

    #endregion

    /// <summary>
    /// 当前进程是否以管理员身份运行。WinRT picker 在这种进程里会抛 E_ACCESSDENIED。
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>多选文件；用户取消返回空列表。</summary>
    public static IReadOnlyList<string> OpenFiles(IntPtr owner, string title,
                                                  IReadOnlyList<string> extensions,
                                                  bool allowAll = true)
    {
        string filter = BuildFilter(extensions, allowAll);
        // 多选时缓冲区格式为 "目录\0文件1\0文件2\0\0"
        var buffer = new string('\0', 64 * 1024);
        var ofn = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            DlgOwner = owner,
            Filter = filter,
            FilterIndex = 1,
            File = buffer,
            MaxFile = buffer.Length,
            Title = title,
            Flags = OFN_EXPLORER | OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST |
                    OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
        };
        if (!GetOpenFileNameW(ref ofn)) return Array.Empty<string>();
        return ParseMultiSelect(ofn.File ?? "");
    }

    /// <summary>保存文件；取消返回 null。</summary>
    public static string? SaveFile(IntPtr owner, string title, string suggestedName, string defaultExt)
    {
        var buffer = new StringBuilder(suggestedName, 4096);
        var ofn = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            DlgOwner = owner,
            Filter = "CSV (*.csv)\0*.csv\0所有文件 (*.*)\0*.*\0\0",
            FilterIndex = 1,
            File = buffer.ToString(),
            MaxFile = 4096,
            Title = title,
            DefExt = defaultExt.TrimStart('.'),
            Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
        };
        if (!GetSaveFileNameW(ref ofn)) return null;
        string result = (ofn.File ?? "").Trim('\0');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>选择文件夹；取消返回 null。</summary>
    public static string? PickFolder(IntPtr owner, string title)
    {
        var bi = new BrowseInfo
        {
            Owner = owner,
            Title = title,
            Flags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
        };
        IntPtr pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(260);
            return SHGetPathFromIDListW(pidl, path) ? path.ToString() : null;
        }
        finally { CoTaskMemFree(pidl); }
    }

    private static string BuildFilter(IReadOnlyList<string> extensions, bool allowAll)
    {
        var sb = new StringBuilder();
        string patterns = string.Join(";", extensions.Where(e => e.StartsWith('.')).Select(e => "*" + e));
        if (patterns.Length > 0)
            sb.Append("支持的格式 (").Append(patterns).Append(")\0").Append(patterns).Append("\0");
        if (allowAll) sb.Append("所有文件 (*.*)\0*.*\0");
        sb.Append('\0');
        return sb.ToString();
    }

    private static List<string> ParseMultiSelect(string raw)
    {
        var parts = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new List<string>();
        if (parts.Length == 1) return new List<string> { parts[0] };
        string dir = parts[0];
        return parts.Skip(1)
            .Select(name => Path.Combine(dir, name))
            .ToList();
    }
}
