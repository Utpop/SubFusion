using System.Runtime.InteropServices;
using System.Text;

namespace SubFusion.App.Services;

/// <summary>
/// 经典 Win32 文件对话框（GetOpenFileNameW / GetSaveFileNameW / SHBrowseForFolder）。
///
/// 为什么需要它：WinRT 的 <c>FileOpenPicker</c> / <c>FolderPicker</c> 在**提升（管理员）进程**里
/// 不可用——它们依赖未提升的 Shell 代理，调用时会以 E_ACCESSDENIED (0x80070005) 失败；
/// 在 WinUI 的 <c>async void</c> 命令里这个异常会直接逃逸成 fail-fast（0xc000027b 崩溃）。
/// 经典 Win32 对话框没有这个限制，因此管理员运行时改走这里。
/// </summary>
internal static class ClassicDialogs
{
    #region COM 互操作

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr DlgOwner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        // GetOpenFileNameW / GetSaveFileNameW 会直接向 lpstrFile 写入多选结果，
        // 因此必须显式分配非托管缓冲区（Marshal.StringToHGlobalUni），
        // 并用 MaxFile 告知缓冲区字符数，避免长路径越界写。
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDir;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefExt;
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

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    private const int FNERR_BUFFERTOOSMALL = 0x3003;
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
        public IntPtr DisplayName;
        public IntPtr Title;
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
        const int bufferChars = 64 * 1024;
        string? raw = ShowFileDialog<string?>(owner, title, BuildFilter(extensions, allowAll),
            "", bufferChars,
            OFN_EXPLORER | OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST |
            OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            buffer => buffer);
        // 取消时不能返回 null：上层 PickFilesAsync / PickExecutableAsync 会直接对结果
        // 取 Count 或 FirstOrDefault()，null 会变成空引用崩溃。
        return raw is null ? Array.Empty<string>() : ParseMultiSelect(raw);
    }

    /// <summary>单选文件；取消返回 null。可执行文件选择走这里：不显示“所有文件”，避免误选非 exe。</summary>
    public static string? OpenSingleFile(IntPtr owner, string title, IReadOnlyList<string> extensions)
    {
        const int bufferChars = 64 * 1024;
        string? raw = ShowFileDialog<string?>(owner, title, BuildFilter(extensions, allowAll: false),
            "", bufferChars,
            OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            buffer => buffer);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>保存文件；取消返回 null。过滤器与默认扩展名均由 defaultExt 生成（mkv/mp4/csv）。</summary>
    public static string? SaveFile(IntPtr owner, string title, string suggestedName, string defaultExt)
    {
        const int bufferChars = 4096;
        string ext = "." + defaultExt.TrimStart('.');
        return ShowFileDialog(owner, title, BuildFilter(new[] { ext }, allowAll: true),
            suggestedName, bufferChars,
            OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            buffer => string.IsNullOrWhiteSpace(buffer) ? null : buffer,
            defaultExt.TrimStart('.'));
    }

    private static T ShowFileDialog<T>(IntPtr owner, string title, string filter,
                                       string initialFile, int bufferChars, int flags,
                                       Func<string, T> parse, string defExt = "")
    {
        // lpstrFile 缓冲区按 UTF-16 字符计；末尾多留一个字符给第二个 NUL。
        IntPtr fileBuffer = Marshal.AllocHGlobal((bufferChars + 1) * 2);
        IntPtr filterPtr = Marshal.StringToHGlobalUni(filter);
        IntPtr titlePtr = Marshal.StringToHGlobalUni(title);
        IntPtr defExtPtr = string.IsNullOrEmpty(defExt)
            ? IntPtr.Zero : Marshal.StringToHGlobalUni(defExt);
        try
        {
            // 全零初始化，保证用户取消或未写满时缓冲区以 NUL 结尾。
            for (int i = 0; i <= bufferChars; i++)
                Marshal.WriteInt16(fileBuffer, i * 2, 0);
            if (!string.IsNullOrEmpty(initialFile))
            {
                char[] initial = initialFile.ToCharArray();
                Marshal.Copy(initial, 0, fileBuffer, Math.Min(initial.Length, bufferChars));
            }

            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                DlgOwner = owner,
                Filter = filterPtr,
                FilterIndex = 1,
                File = fileBuffer,
                MaxFile = bufferChars,
                Title = titlePtr,
                Flags = flags,
                DefExt = defExtPtr,
            };
            bool ok = (flags & OFN_OVERWRITEPROMPT) != 0
                ? GetSaveFileNameW(ref ofn)
                : GetOpenFileNameW(ref ofn);
            if (!ok)
            {
                // 多选文件过多时缓冲区不足（FNERR_BUFFERTOOSMALL），不能把空结果当成取消。
                int err = CommDlgExtendedError();
                if (err == FNERR_BUFFERTOOSMALL)
                    throw new IOException("选择的文件过多，请分批添加。");
                return default!;
            }
            return parse(ReadMultiSz(fileBuffer, bufferChars));
        }
        finally
        {
            if (defExtPtr != IntPtr.Zero) Marshal.FreeHGlobal(defExtPtr);
            Marshal.FreeHGlobal(titlePtr);
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(fileBuffer);
        }
    }

    /// <summary>把多选缓冲区里的 "DIR\0F1\0F2\0\0" 读成字符串（供 ParseMultiSelect 分割）。</summary>
    private static string ReadMultiSz(IntPtr buffer, int maxChars)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxChars; i++)
        {
            char c = (char)Marshal.ReadInt16(buffer, i * 2);
            if (c == '\0')
            {
                // 多选结果以两个连续 NUL 结尾；单选/保存路径只有一个 NUL。
                if (i + 1 >= maxChars || Marshal.ReadInt16(buffer, (i + 1) * 2) == 0) break;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>选择文件夹；取消返回 null。</summary>
    public static string? PickFolder(IntPtr owner, string title)
    {
        IntPtr titlePtr = Marshal.StringToHGlobalUni(title);
        IntPtr display = Marshal.AllocHGlobal(260 * 2);
        try
        {
            for (int i = 0; i < 260; i++) Marshal.WriteInt16(display, i * 2, 0);
            var bi = new BrowseInfo
            {
                Owner = owner,
                DisplayName = display,
                Title = titlePtr,
                Flags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
            };
            IntPtr pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero) return null;
            try
            {
                // MAX_PATH 限制已由 app.manifest 的 longPathAware 放宽，用 32K 字符兜底长路径。
                var path = new StringBuilder(32768);
                return SHGetPathFromIDListW(pidl, path) ? path.ToString() : null;
            }
            finally { CoTaskMemFree(pidl); }
        }
        finally
        {
            Marshal.FreeHGlobal(display);
            Marshal.FreeHGlobal(titlePtr);
        }
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
